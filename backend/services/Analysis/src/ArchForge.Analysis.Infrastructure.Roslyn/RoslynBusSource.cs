using System.Text.RegularExpressions;
using ArchForge.Analysis.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchForge.Analysis.Infrastructure.Roslyn;

// Направление pub/sub шины сообщений из кода (#61). Чисто СИНТАКСИЧЕСКИЙ разбор (как RoslynGuardSource), по одному файлу за раз
// (деревья не копим). Честность (adr/0008): сайт засчитываем только в файле, который ПОДКЛЮЧАЕТ библиотеку шины (using) —
// иначе `x.Publish(...)` это свой метод, а MediatR `Publish` — in-process, не шина. Чего не распознали — не выдумываем.
//
//  • потребитель: маркер `IConsumer<T>` (MassTransit), `IHandleMessages<T>` (NServiceBus/Rebus), `bus.Subscribe<T>()` (Rebus),
//    `[CapSubscribe("топик")]` (CAP) — высокая уверенность; Wolverine без маркера — класс *Handler/*Consumer с методом Handle/Consume(T …) — низкая;
//  • издатель: `Publish/PublishAsync` с типом события (`Publish<T>`, `Publish(new T…)`) — высокая; со строкой-топиком (CAP) — средняя;
//    без вывода типа — направление известно, имя нет (низкая). Получатель проверяется по ТИПУ из объявления в файле: IMediator/IPublisher/ISender
//    (MediatR) — in-process, не шина; известный тип клиента шины — издатель; прочий известный тип (свой IEventBus) и dispatcher — Unknown;
//  • «сырые» клиенты: Kafka (Subscribe/Produce*), RabbitMQ (BasicPublish/BasicConsume), Azure Service Bus (CreateSender/Processor/Receiver) —
//    направление по методу, имя топика/очереди — если безопасный литерал (строковая, средняя);
//  • НЕИЗВЕСТНОЕ направление (сайт Unknown на файл): сага/state machine, Send/request-response, Assign/Consume/EventingBasicConsumer без
//    Subscribe в файле, публикация через неопознанный тип, клиент шины без распознанного использования. Шина по такому сервису неполная.
//  • усечение: кап файлов (EnumerateCsCapped) или кап мест на сервис → сайт Truncated (Unknown): скан выглядит неполным, а не полным.
// Не видим: `global using` и полные имена без `using` (файл не считается подключающим шину) — ограничение, см. docs/product.
// Стоимость: префильтр по тексту; один обход узлов на файл; для получателя берём только идентификатор (без ToString на цепочках вызовов);
// отмена в цикле по узлам.
public sealed class RoslynBusSource : IBusSource
{
    private const int DefaultMaxSites = 500;
    private const int MaxNameLength = 100;

    private static readonly string[] Keywords = ["MassTransit", "Wolverine", "NServiceBus", "Rebus", "CAP", "RabbitMQ", "Kafka", "ServiceBus"];
    private static readonly string[] Frameworks = ["MassTransit", "Wolverine", "NServiceBus", "Rebus", "DotNetCore.CAP"];
    private static readonly string[] RawClients = ["RabbitMQ.Client", "Confluent.Kafka", "Azure.Messaging.ServiceBus"];
    private static readonly HashSet<string> MediatrTypes = new(StringComparer.Ordinal) { "IMediator", "IPublisher", "ISender", "Mediator", "INotificationPublisher" };
    private static readonly HashSet<string> BusTypes = new(StringComparer.Ordinal)
    {
        "IPublishEndpoint", "IBus", "IBusControl", "ConsumeContext", "IMessageBus", "IMessageContext", "IMessageSession", "IEndpointInstance",
        "IMessageHandlerContext", "IPipelineContext", "ICapPublisher", "ISendEndpoint", "ISendEndpointProvider", "IBusInstance",
    };
    // безопасное имя топика/очереди: без пробелов, строк подключения и URL (secrets-config)
    private static readonly Regex SafeTopic = new(@"^[A-Za-z0-9][A-Za-z0-9._\-:#*/]{0,99}\z", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private readonly int _maxFiles;
    private readonly int _maxSites;

    public RoslynBusSource() : this(RoslynScan.MaxFilesPerScan, DefaultMaxSites) { }
    internal RoslynBusSource(int maxFiles, int maxSites) { _maxFiles = maxFiles; _maxSites = maxSites; }

    public IReadOnlyList<BusSite> Read(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return [];
        var sites = new List<BusSite>();
        string? frameworkFile = null;
        string? truncatedAt = null;
        var seen = new HashSet<(BusDirection, string, string, int)>();
        var files = RoslynScan.EnumerateCsCapped(root, out var capped, _maxFiles);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (sites.Count >= _maxSites) { truncatedAt = file; break; }
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            if (!Keywords.Any(k => text.Contains(k, StringComparison.Ordinal))) continue;

            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);
            var imports = tree.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .OfType<UsingDirectiveSyntax>().Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)).Select(u => u.Name?.ToString() ?? "").ToList();
            bool Has(string ns) => imports.Any(i => i == ns || i.StartsWith(ns + ".", StringComparison.Ordinal));
            var framework = Frameworks.Any(Has);
            var raw = RawClients.Any(Has);
            if (!framework && !raw) continue;
            if (framework) frameworkFile ??= file;

            var ctx = new Ctx(Has("MassTransit"), Has("Wolverine"), Has("NServiceBus"), Has("Rebus"), Has("DotNetCore.CAP"),
                Has("Confluent.Kafka"), Has("RabbitMQ.Client"), Has("Azure.Messaging.ServiceBus"), Has("MediatR"));
            var path = file.Replace('\\', '/');
            var before = sites.Count;
            var state = new FileState(tree);
            foreach (var node in tree.DescendantNodes())
            {
                ct.ThrowIfCancellationRequested();
                if (sites.Count >= _maxSites) { truncatedAt = file; break; }
                switch (node)
                {
                    case TypeDeclarationSyntax td:
                        ReadType(td, ctx, path, sites, seen);
                        break;
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv:
                        ReadInvocation(inv, ma, ctx, state, path, sites, seen);
                        break;
                    case ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax { Identifier.Text: "EventingBasicConsumer" or "AsyncEventingBasicConsumer" } } when ctx.Rabbit:
                        state.NeedsSubscribe = (path, node);
                        break;
                    case AttributeSyntax at when ctx.Cap && at.Name is IdentifierNameSyntax { Identifier.Text: "CapSubscribe" or "CapSubscribeAttribute" }:
                    {
                        var topic = Literal(at.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
                        Add(sites, seen, BusDirection.Subscribe, topic ?? "", path, at, topic is null ? BusBasis.Unresolved : BusBasis.Marker, topic is null ? 0.4 : 0.9);
                        break;
                    }
                }
            }
            var fileSubscribes = sites.Skip(before).Any(x => x.Direction == BusDirection.Subscribe);
            // Assign/Consume/EventingBasicConsumer без Subscribe/BasicConsume в файле — потребление нераспознанным способом
            if (state.NeedsSubscribe is { } ns && !fileSubscribes) AddUnknown(sites, seen, ns.Path, ns.Node);
            // клиент шины подключён, а направление в файле не распознано → шина по сервису неполная
            if (raw && sites.Count == before) AddUnknown(sites, seen, path, null);
        }
        // библиотека фреймворка подключена, а ни одного сайта нет: потребители/издатели могут быть по соглашениям, которых мы не знаем
        if (frameworkFile is not null && sites.Count == 0)
            sites.Add(new BusSite(BusDirection.Unknown, "", frameworkFile.Replace('\\', '/'), 1, BusBasis.Unresolved, 0));
        // кап файлов или мест сработал — часть кода не прочитана: честно помечаем (в обход капа мест)
        if (capped || truncatedAt is not null)
            sites.Add(new BusSite(BusDirection.Unknown, "", (truncatedAt ?? root).Replace('\\', '/'), 1, BusBasis.Truncated, 0));
        return sites;
    }

    private sealed record Ctx(bool MassTransit, bool Wolverine, bool NServiceBus, bool Rebus, bool Cap, bool Kafka, bool Rabbit, bool AzureSb, bool MediatR)
    {
        public bool Framework => MassTransit || Wolverine || NServiceBus || Rebus || Cap;
    }

    // Состояние одного файла: типы объявленных членов/параметров и имена методов — лениво, одним проходом (для получателя и Rx-фильтра).
    private sealed class FileState(SyntaxNode root)
    {
        public (string Path, SyntaxNode Node)? NeedsSubscribe;
        private Dictionary<string, string>? _types;
        private HashSet<string>? _methods;

        public string? TypeOf(string name)
        {
            if (name.Length == 0) return null;
            if (_types is null)
            {
                _types = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var n in root.DescendantNodes())
                {
                    switch (n)
                    {
                        case FieldDeclarationSyntax f:
                            foreach (var v in f.Declaration.Variables) Put(v.Identifier.Text, f.Declaration.Type);
                            break;
                        case PropertyDeclarationSyntax p: Put(p.Identifier.Text, p.Type); break;
                        case ParameterSyntax { Type: { } t } prm: Put(prm.Identifier.Text, t); break;
                    }
                }
            }
            return _types.TryGetValue(name, out var r) && r.Length > 0 ? r : null;
        }

        private void Put(string name, TypeSyntax type)
        {
            var t = type is GenericNameSyntax or IdentifierNameSyntax or QualifiedNameSyntax or NullableTypeSyntax ? RoslynScan.Simple(type) : "";
            // одно имя с разными типами в разных областях — неоднозначно: тип неизвестен
            _types![name] = _types.TryGetValue(name, out var old) && old != t ? "" : t;
        }

        public bool IsOwnMethod(string name) =>
            (_methods ??= [.. root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text)]).Contains(name);
    }

    // ── типы: маркеры потребителя, сага и Wolverine-соглашение ──

    private static void ReadType(TypeDeclarationSyntax td, Ctx ctx, string path, List<BusSite> sites, HashSet<(BusDirection, string, string, int)> seen)
    {
        foreach (var b in td.BaseList?.Types ?? default)
        {
            // сага / state machine — потребляет сообщения неочевидным способом
            if (b.Type is GenericNameSyntax { Identifier.Text: "MassTransitStateMachine" or "Saga" or "SagaStateMachine" } or IdentifierNameSyntax { Identifier.Text: "MassTransitStateMachine" or "ISaga" })
            {
                AddUnknown(sites, seen, path, b);
                continue;
            }
            if (b.Type is not GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } g) continue;
            var isConsumer = (g.Identifier.Text == "IConsumer" && ctx.MassTransit)
                || (g.Identifier.Text is "IHandleMessages" && (ctx.NServiceBus || ctx.Rebus));
            if (!isConsumer) continue;
            var arg = g.TypeArgumentList.Arguments[0];
            if (arg is GenericNameSyntax { Identifier.Text: "Batch", TypeArgumentList.Arguments.Count: 1 } batch) arg = batch.TypeArgumentList.Arguments[0];
            Add(sites, seen, BusDirection.Subscribe, TypeName(arg), path, b, BusBasis.Marker, 0.9);
        }
        if (!ctx.Wolverine || td is not ClassDeclarationSyntax cd) return;
        var attributed = cd.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name is IdentifierNameSyntax { Identifier.Text: "WolverineHandler" or "WolverineHandlerAttribute" });
        var named = cd.Identifier.Text.EndsWith("Handler", StringComparison.Ordinal) || cd.Identifier.Text.EndsWith("Consumer", StringComparison.Ordinal);
        if (!attributed && !named) return;
        foreach (var m in cd.Members.OfType<MethodDeclarationSyntax>())
        {
            if (m.Identifier.Text is not ("Handle" or "HandleAsync" or "Consume" or "ConsumeAsync")) continue;
            if (m.ParameterList.Parameters.FirstOrDefault()?.Type is not { } pt || pt is PredefinedTypeSyntax) continue;
            var name = TypeName(pt);
            if (name is "CancellationToken" or "") continue;
            Add(sites, seen, BusDirection.Subscribe, name, path, m, attributed ? BusBasis.Marker : BusBasis.Convention, attributed ? 0.8 : 0.5);
        }
    }

    // ── вызовы: публикации, сырые клиенты, нераспознанное потребление ──

    private static void ReadInvocation(InvocationExpressionSyntax inv, MemberAccessExpressionSyntax ma, Ctx ctx, FileState state, string path,
        List<BusSite> sites, HashSet<(BusDirection, string, string, int)> seen)
    {
        var method = ma.Name.Identifier.Text;
        var args = inv.ArgumentList.Arguments;
        var first = args.Count > 0 ? args[0].Expression : null;
        switch (method)
        {
            case "Publish" or "PublishAsync" when ctx.Framework:
            {
                switch (Receiver(ma.Expression, ctx, state))
                {
                    case ReceiverKind.InProcess: return;                                       // MediatR — не шина
                    case ReceiverKind.Opaque: AddUnknown(sites, seen, path, inv); return;      // своя обёртка/диспетчер — куда уходит, неизвестно
                }
                if (ma.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } g)
                    Add(sites, seen, BusDirection.Publish, TypeName(g.TypeArgumentList.Arguments[0]), path, inv, BusBasis.Marker, 0.8);
                else if (Literal(first) is { } topic && ctx.Cap)
                    Add(sites, seen, BusDirection.Publish, topic, path, inv, BusBasis.String, 0.6);
                else if (first is ObjectCreationExpressionSyntax oc)
                    Add(sites, seen, BusDirection.Publish, TypeName(oc.Type), path, inv, BusBasis.Marker, 0.8);
                else
                    Add(sites, seen, BusDirection.Publish, "", path, inv, BusBasis.Unresolved, 0.4);   // направление есть, тип не выведен
                return;
            }
            case "Send" or "SendAsync" or "GetResponse" or "CreateRequestClient" when ctx.Framework:
                // команды и request/response: потребитель на другой стороне, как он устроен — неизвестно
                if (Receiver(ma.Expression, ctx, state) != ReceiverKind.InProcess) AddUnknown(sites, seen, path, inv);
                return;
            case "ConsumeAsync" when ctx.Framework:
                AddUnknown(sites, seen, path, inv);
                return;
            case "Subscribe" when ctx.Rebus && ma.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } rg:
                Add(sites, seen, BusDirection.Subscribe, TypeName(rg.TypeArgumentList.Arguments[0]), path, inv, BusBasis.Marker, 0.8);
                return;
            case "Subscribe" when ctx.Kafka:
                // Rx-подписка (лямбда или метод-группа своего класса) — не Kafka
                if (first is null or LambdaExpressionSyntax) return;
                if (first is IdentifierNameSyntax { Identifier.Text: var mg } && state.IsOwnMethod(mg)) return;
                if (first is MemberAccessExpressionSyntax { Name.Identifier.Text: var mg2 } && state.IsOwnMethod(mg2)) return;
                AddNamed(sites, seen, BusDirection.Subscribe, Literal(first), path, inv);
                return;
            case "Assign" or "Consume" when ctx.Kafka:
                state.NeedsSubscribe ??= (path, inv);
                return;
            case "Produce" or "ProduceAsync" when ctx.Kafka:
                AddNamed(sites, seen, BusDirection.Publish, Literal(first), path, inv);
                return;
            case "BasicPublish" or "BasicPublishAsync" when ctx.Rabbit:
                AddNamed(sites, seen, BusDirection.Publish, NamedLiteral(args, "routingKey") ?? (args.Count > 1 ? Literal(args[1].Expression) : null), path, inv);
                return;
            case "BasicConsume" when ctx.Rabbit:
                AddNamed(sites, seen, BusDirection.Subscribe, NamedLiteral(args, "queue") ?? Literal(first), path, inv);
                return;
            case "CreateSender" when ctx.AzureSb:
                AddNamed(sites, seen, BusDirection.Publish, Literal(first), path, inv);
                return;
            case "CreateProcessor" or "CreateReceiver" when ctx.AzureSb:
                AddNamed(sites, seen, BusDirection.Subscribe, Literal(first), path, inv);
                return;
        }
    }

    private enum ReceiverKind { Bus, InProcess, Opaque }

    // Получатель Publish/Send: по типу из объявления в файле; если тип неизвестен — по имени.
    private static ReceiverKind Receiver(ExpressionSyntax e, Ctx ctx, FileState state)
    {
        var name = ReceiverName(e);
        var type = state.TypeOf(name);
        if (type is not null)
        {
            if (MediatrTypes.Contains(type)) return ReceiverKind.InProcess;
            return BusTypes.Contains(type) ? ReceiverKind.Bus : ReceiverKind.Opaque;
        }
        if (name.Contains("mediator", StringComparison.OrdinalIgnoreCase)) return ReceiverKind.InProcess;
        if (ctx.MediatR && name.Contains("publisher", StringComparison.OrdinalIgnoreCase)) return ReceiverKind.InProcess;
        if (name.Contains("dispatcher", StringComparison.OrdinalIgnoreCase) || name.Contains("eventbus", StringComparison.OrdinalIgnoreCase)) return ReceiverKind.Opaque;
        return ReceiverKind.Bus;
    }

    private static void AddNamed(List<BusSite> sites, HashSet<(BusDirection, string, string, int)> seen, BusDirection dir, string? literal, string path, SyntaxNode at) =>
        Add(sites, seen, dir, literal ?? "", path, at, literal is null ? BusBasis.Unresolved : BusBasis.String, literal is null ? 0.4 : 0.6);

    private static void AddUnknown(List<BusSite> sites, HashSet<(BusDirection, string, string, int)> seen, string path, SyntaxNode? at)
    {
        var line = at is null ? 1 : at.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (seen.Add((BusDirection.Unknown, "", path, line))) sites.Add(new BusSite(BusDirection.Unknown, "", path, line, BusBasis.Unresolved, 0));
    }

    private static void Add(List<BusSite> sites, HashSet<(BusDirection, string, string, int)> seen, BusDirection dir, string name, string path,
        SyntaxNode at, BusBasis basis, double confidence)
    {
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        var line = at.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (!seen.Add((dir, name, path, line))) return;
        sites.Add(new BusSite(dir, name, path, line, basis, confidence));
    }

    // Простое имя типа; без вывода строки из длинных конструкций (Simple для редких форм берёт короткий текст типа).
    private static string TypeName(TypeSyntax t) => t is GenericNameSyntax or IdentifierNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax or NullableTypeSyntax
        ? RoslynScan.Simple(t) : "";

    // Идентификатор получателя вызова без ToString на цепочках: `bus.Publish` → bus, `this._bus.Publish` → _bus, прочее → "".
    private static string ReceiverName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => "",
    };

    // Строковый литерал — только если похож на имя топика/очереди: строки подключения и URL в ответ не попадают (secrets-config).
    private static string? Literal(ExpressionSyntax? e)
    {
        if (e is not LiteralExpressionSyntax l || !l.IsKind(SyntaxKind.StringLiteralExpression)) return null;
        var v = l.Token.ValueText;
        try { return SafeTopic.IsMatch(v) && !v.Contains("://", StringComparison.Ordinal) ? v : null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static string? NamedLiteral(SeparatedSyntaxList<ArgumentSyntax> args, string name)
    {
        foreach (var a in args)
            if (a.NameColon?.Name.Identifier.Text == name) return Literal(a.Expression);
        return null;
    }
}
