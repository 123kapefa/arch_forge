using ArchForge.Analysis.Core;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Правило «события в агрегате»: публичная мутация агрегата поднимает доменное событие и обновляет UpdatedAt.
public class RoslynAggregateEventSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_events_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private IReadOnlyList<EventSite> Scan(string code, string file = "src/Svc.Domain/Widget.cs")
    {
        var p = Path.Combine(_root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, code);
        return new RoslynAggregateEventSource().Read(_root).Sites;
    }

    [Fact]
    public void Mutation_without_event_and_without_timestamp_is_found_ok_methods_are_not()
    {
        var f = Scan("""
            class Widget : AggregateRoot {
                public string Name { get; private set; }
                public int Count { get; private set; }
                public DateTime UpdatedAt { get; private set; }
                public static Widget Create(string n) { var w = new Widget(); w.Name = n; return w; }
                public Widget() { Name = "x"; }
                public void Rename(string n, DateTime now) { Name = n; UpdatedAt = now; RaiseDomainEvent(new Renamed(n, now)); }
                public void Bump() { Count++; }
                public void Silent(string n, DateTime now) { Name = n; UpdatedAt = now; }
                public void NoTime(string n) { this.Name = n; RaiseDomainEvent(new Renamed(n)); }
                public void Read() { var x = Name; }
                private void Helper() { Name = "h"; }
            }
            """);
        Assert.Collection(f.OrderBy(s => s.Member, StringComparer.Ordinal),
            s => { Assert.Equal("Bump", s.Member); Assert.Equal(EventGap.NoEvent | EventGap.NoTimestamp, s.Gap); Assert.Equal("UpdatedAt", s.Timestamp); },
            s => { Assert.Equal("NoTime", s.Member); Assert.Equal(EventGap.NoTimestamp, s.Gap); },
            s => { Assert.Equal("Silent", s.Member); Assert.Equal(EventGap.NoEvent, s.Gap); Assert.Equal("Widget", s.Type); Assert.True(s.Line > 0); });
    }

    [Fact]
    public void Calling_an_own_method_that_raises_or_stamps_counts()
    {
        var f = Scan("""
            class Widget : AggregateRoot {
                public string Name { get; private set; }
                public DateTime UpdatedAt { get; private set; }
                public void Rename(string n, DateTime now) { Name = n; Touch(now); Raise(n, now); }
                private void Touch(DateTime now) { UpdatedAt = now; }
                private void Raise(string n, DateTime now) { RaiseDomainEvent(new Renamed(n, now)); }
            }
            """);
        Assert.Empty(f);
    }

    [Fact]
    public void Type_that_raises_somewhere_is_an_aggregate_event_list_add_and_collections_count()
    {
        var f = Scan("""
            class Order {
                private readonly List<Line> _lines = new();
                private readonly List<object> _domainEvents = new();
                public Status Status { get; private set; }
                public void Confirm() { Status = Status.Confirmed; _domainEvents.Add(new Confirmed()); }
                public void AddLine(Line l) { _lines.Add(l); }
                public void Cancel() { this.Status = Status.Cancelled; }
            }
            """);
        Assert.Equal(["AddLine", "Cancel"], f.Select(s => s.Member).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(f, s => Assert.Equal(EventGap.NoEvent, s.Gap));
    }

    [Fact]
    public void Plain_entities_vo_public_setters_shadowing_partial_and_event_sourcing_are_not_judged()
    {
        // нет событий и нет базы AggregateRoot — не агрегат; публичные сеттеры (DTO) — не состояние агрегата
        Assert.Empty(Scan("class Plain { public string Name { get; private set; } public void Rename(string n) { Name = n; } }"));
        Assert.Empty(Scan("class Dto { public string Name { get; set; } public void Fill(string n) { Name = n; } }"));
        // параметр затеняет поле, локальная переменная тоже
        Assert.Empty(Scan("class W : AggregateRoot { private string name; public void Set(string name) { name = name; } public void L() { var name = 1; name = 2; } }"));
        // partial: метод с событием может лежать в другой части
        Assert.Empty(Scan("partial class W : AggregateRoot { public string N { get; private set; } public void Set(string n) { N = n; } }"));
        // event sourcing: состояние меняет Apply/When
        Assert.Empty(Scan("class W : AggregateRoot { public string N { get; private set; } public void Apply(Renamed e) { N = e.N; } public void WhenRenamed(Renamed e) { N = e.N; } }"));
        // статические и абстрактные
        Assert.Empty(Scan("class W : AggregateRoot { static string s; public string N { get; private set; } public static void F() { s = \"a\"; } }"));
    }

    [Fact]
    public void Timestamp_is_checked_only_when_the_type_sets_it_itself()
    {
        // UpdatedAt ставит interceptor (никто в типе не ставит) — это не про это правило
        var f = Scan("class W : AggregateRoot { public string N { get; private set; } public DateTime UpdatedAt { get; private set; } public void Set(string n) { N = n; RaiseDomainEvent(new E()); } }");
        Assert.Empty(f);
    }

    [Fact]
    public void Only_the_domain_layer_is_scanned()
    {
        const string code = "class W : AggregateRoot { public string N { get; private set; } public void Set(string n) { N = n; } }";
        Assert.Empty(Scan(code, "src/Svc.Core/W.cs"));
        Assert.Empty(Scan(code, "src/Svc.Domain.Tests/W.cs"));
        Assert.Single(Scan(code, "src/Svc.Domain/W.cs"));
    }

    [Fact]
    public void Hostile_input_is_bounded_and_cancellation_is_honored()
    {
        var methods = string.Concat(Enumerable.Range(0, 800).Select(i => $"public void M{i}() {{ N = {i}; }}\n"));
        var f = Scan("class " + new string('A', 5000) + " : AggregateRoot { public int N { get; private set; }\n" + methods + "}");
        Assert.InRange(f.Count, 1, 200);
        Assert.All(f, s => Assert.True(s.Type.Length <= 201));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new RoslynAggregateEventSource().Read(_root, cts.Token));
    }

    [Fact]
    public void Broken_syntax_and_missing_folder_do_not_throw()
    {
        Assert.Empty(Scan("class W : AggregateRoot { public void ( { N = "));
        Assert.Empty(new RoslynAggregateEventSource().Read(Path.Combine(_root, "nope")).Sites);
    }
}
