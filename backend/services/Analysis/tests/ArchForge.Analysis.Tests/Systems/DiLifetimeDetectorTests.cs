using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests.Systems;

// E4.2: детектор DI на живом синтаксическом разборе (RoslynDiSource → DiLifetimeDetector): captive, missing и ложноположительные.
public class DiLifetimeDetectorTests
{
    private static IReadOnlyList<SystemProblem> Run(params (string Name, string Code)[] files)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "af_di_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var (name, code) in files)
            {
                var full = Path.Combine(tmp, name);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, code);
            }
            var a = new ServiceAnalysis(new DiscoveredService("orders-api", tmp, null), "sys/orders", null) { Di = new RoslynDiSource().Read(tmp) };
            return DiLifetimeDetector.Detect([a]);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    private static IReadOnlyList<SystemProblem> Run(string program) => Run(("Program.cs", program));

    private const string Types = """
        public class AppDbContext { }
        public interface IPaymentService { }
        public class PaymentService : IPaymentService { public PaymentService(AppDbContext db) { } }
        """;

    [Fact]
    public void Singleton_holding_scoped_dbcontext_is_captive_with_source()
    {
        var p = Assert.Single(Run($$"""
            {{Types}}
            class P { void M(Microsoft.Extensions.DependencyInjection.IServiceCollection services) {
                services.AddDbContext<AppDbContext>();
                services.AddSingleton<IPaymentService, PaymentService>();
            } }
            """));
        Assert.Equal("di-captive", p.Kind);
        Assert.Equal(ProblemSeverity.Warning, p.Severity);
        Assert.Equal(ProblemCategory.Architecture, p.Category);
        Assert.Equal("sys/orders", p.Location);
        var it = Assert.Single(p.Items!);
        Assert.Equal("PaymentService → AppDbContext", it.Name);
        Assert.Equal("Singleton → Scoped", it.Layer);
        Assert.EndsWith("Program.cs", it.File);
        Assert.Equal(6, it.Line);                       // строка регистрации Singleton
        Assert.Contains("высокая", it.Note);
    }

    [Fact]
    public void Singleton_through_transient_reaching_scoped_is_found_with_chain()
    {
        var p = Assert.Single(Run("""
            public class Db { }
            public class Mapper { public Mapper(Db d) { } }
            public class Cache { public Cache(Mapper m) { } }
            class P { void M(IServiceCollection s) {
                s.AddScoped<Db>(); s.AddTransient<Mapper>(); s.AddSingleton<Cache>();
            } }
            """), x => x.Kind == "di-captive");
        Assert.Contains(p.Items!, i => i.Name == "Cache → Mapper → Db" && i.Layer == "Singleton → Scoped");
        Assert.DoesNotContain(p.Items!, i => i.Layer == "Singleton → Transient");   // Transient — отдельный мягкий вид
        var hint = Assert.Single(Run("""
            public class Db { } public class Mapper { public Mapper(Db d) { } } public class Cache { public Cache(Mapper m) { } }
            class P { void M(IServiceCollection s) { s.AddScoped<Db>(); s.AddTransient<Mapper>(); s.AddSingleton<Cache>(); } }
            """), x => x.Kind == "di-lifetime-hint");
        Assert.Equal(ProblemSeverity.Info, hint.Severity);                            // здоровье не красит
        Assert.Contains(hint.Items!, i => i.Name == "Cache → Mapper" && i.Layer == "Singleton → Transient");
    }

    [Fact]
    public void Scoped_and_transient_roots_and_singleton_over_singleton_are_fine()
    {
        Assert.Empty(Run("""
            public class Db { } public class A { public A(Db d) { } } public class B { public B(A a) { } }
            public class S1 { } public class S2 { public S2(S1 a) { } }
            class P { void M(IServiceCollection s) {
                s.AddScoped<Db>(); s.AddScoped<A>(); s.AddTransient<B>();   // Scoped/Transient над Scoped — нормально
                s.AddSingleton<S1>(); s.AddSingleton<S2>();
            } }
            """));
    }

    [Fact]
    public void Unregistered_own_interface_is_missing_with_registration_source()
    {
        var p = Assert.Single(Run("""
            public interface IPasswordHasher { }
            public class AuthService { public AuthService(IPasswordHasher h) { } }
            class P { void M(IServiceCollection s) {
                s.AddScoped<AuthService>();
            } }
            """));
        Assert.Equal("di-missing", p.Kind);
        Assert.Equal(ProblemSeverity.Warning, p.Severity);
        var it = Assert.Single(p.Items!);
        Assert.Equal("AuthService → IPasswordHasher", it.Name);
        Assert.Equal(4, it.Line);
        Assert.Contains("высокая", it.Note);
    }

    [Fact]
    public void Controller_dependency_without_registration_is_missing()
    {
        var p = Assert.Single(Run("""
            public interface IOrders { }
            public class OrdersController : ControllerBase { public OrdersController(IOrders o) { } }
            class P { void M(IServiceCollection s) { s.AddScoped<P>(); } }
            """));
        Assert.Equal("di-missing", p.Kind);
        Assert.Equal("OrdersController → IOrders", Assert.Single(p.Items!).Name);
    }

    [Fact]
    public void Framework_types_optional_params_and_registered_things_are_not_flagged()
    {
        Assert.Empty(Run("""
            public interface IRepo<T> { } public class Repo<T> : IRepo<T> { }
            public interface IPlugin { } public interface IClock { } public enum Mode { A }
            public record Dto(int X);
            public class Svc {
                public Svc(ILogger<Svc> log, IOptions<Svc> o, IConfiguration c, IRepo<Dto> r, IEnumerable<IPlugin> plugins,
                           IClock? clock = null, string name = "", Func<IClock> f = null) { }
            }
            class P { void M(IServiceCollection s) {
                s.AddSingleton(typeof(IRepo<>), typeof(Repo<>));   // открытый дженерик
                s.AddSingleton<Svc>();
            } }
            """));
    }

    [Fact]
    public void Scrutor_scan_downgrades_missing_to_low_confidence_coverage()
    {
        var p = Assert.Single(Run("""
            public interface IFoo { }
            public class Bar { public Bar(IFoo f) { } }
            class P { void M(IServiceCollection s) {
                s.AddScoped<Bar>();
                s.Scan(x => x.FromAssemblyOf<P>().AddClasses().AsImplementedInterfaces().WithScopedLifetime());
            } }
            """));
        Assert.Equal("di-missing", p.Kind);
        Assert.Equal(ProblemSeverity.Coverage, p.Severity);   // не красит health
        Assert.Contains("низкая", Assert.Single(p.Items!).Note);
    }

    [Fact]
    public void Factory_registration_does_not_trigger_missing_and_captive_is_low_confidence()
    {
        var all = Run("""
            public interface IFoo { } public class Foo { public Foo(IFoo x) { } }
            public class Db { }
            public interface IReport { } public class Report : IReport { public Report(Db d) { } }
            class P { void M(IServiceCollection s) {
                s.AddScoped<Db>();
                s.AddSingleton<Foo>(sp => new Foo(null));                                     // ctor Foo — забота фабрики, не missing
                s.AddSingleton<IReport>(sp => new Report(sp.GetRequiredService<Db>()));       // видно из тела фабрики
            } }
            """);
        Assert.DoesNotContain(all, p => p.Kind == "di-missing");
        var p = Assert.Single(all);
        Assert.Equal("di-lifetime-hint", p.Kind);                  // низкая уверенность → не di-captive
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Contains("низкая", Assert.Single(p.Items!).Note);
    }

    [Fact]
    public void TryAdd_does_not_override_and_last_registration_wins()
    {
        // TryAddSingleton не перебивает уже зарегистрированный Scoped → Singleton Holder держит Scoped
        var p = Assert.Single(Run("""
            public class Dep { } public class Holder { public Holder(Dep d) { } }
            class P { void M(IServiceCollection s) {
                s.AddScoped<Dep>(); s.TryAddSingleton<Dep>(); s.AddSingleton<Holder>();
            } }
            """));
        Assert.Equal("di-captive", p.Kind);
        var it = Assert.Single(p.Items!);
        Assert.Equal("Singleton → Scoped", it.Layer);
        Assert.Contains("высокая", it.Note);                       // TryAdd не перебивает обычную регистрацию и конфликтом не считается
        Assert.Equal(ProblemSeverity.Warning, p.Severity);
        // последняя обычная регистрация побеждает: Dep переопределён в Singleton → Holder не пленник
        Assert.Empty(Run("""
            public class Dep { } public class Holder { public Holder(Dep d) { } }
            class P { void M(IServiceCollection s) {
                s.AddScoped<Dep>(); s.AddSingleton<Dep>(); s.AddSingleton<Holder>();
            } }
            """));
    }

    [Fact]
    public void Hosted_service_over_scoped_and_dbcontext_singleton_lifetime_override()
    {
        var p = Assert.Single(Run("""
            public class Db { } public class AppDbContext { }
            public class Worker { public Worker(AppDbContext d) { } }
            class P { void M(IServiceCollection s) {
                s.AddDbContext<AppDbContext>(o => { });
                s.AddHostedService<Worker>();
            } }
            """));
        Assert.Equal("Worker → AppDbContext", Assert.Single(p.Items!).Name);
        // явный ServiceLifetime.Singleton у контекста — захвата нет
        Assert.Empty(Run("""
            public class AppDbContext { } public class Worker { public Worker(AppDbContext d) { } }
            class P { void M(IServiceCollection s) {
                s.AddDbContext<AppDbContext>(o => { }, ServiceLifetime.Singleton);
                s.AddHostedService<Worker>();
            } }
            """));
    }

    [Fact]
    public void Registrations_in_extension_method_in_other_file_are_seen_and_tests_are_ignored()
    {
        var p = Run(
            ("Program.cs", "class P { void M(IServiceCollection s) { s.AddAppServices(); } }"),
            ("Ext.cs", """
                public static class Ext { public static IServiceCollection AddAppServices(this IServiceCollection s) {
                    s.AddScoped<Db>(); s.AddSingleton<Holder>(); return s; } }
                public class Db { } public class Holder { public Holder(Db d) { } }
                """));
        Assert.Equal("Holder → Db", Assert.Single(Assert.Single(p).Items!).Name);

        // тестовые файлы (каталог Tests/) — не продуктовый контейнер
        Assert.Empty(Run(("Tests/Hosts.cs", "class T { void M(IServiceCollection s) { s.AddSingleton<H>(); } } class H { public H(D d) { } } interface D { }")));
    }

    [Fact]
    public void Two_normal_registrations_with_different_lifetimes_are_medium_confidence_hint()
    {
        var p = Assert.Single(Run("""
            public class Dep { } public class Holder { public Holder(Dep d) { } }
            class P { void M(IServiceCollection s) { s.AddSingleton<Dep>(); s.AddScoped<Dep>(); s.AddSingleton<Holder>(); } }
            """));
        Assert.Equal("di-lifetime-hint", p.Kind);
        Assert.Equal(ProblemSeverity.Info, p.Severity);
        Assert.Contains("средняя", Assert.Single(p.Items!).Note);
    }

    [Fact]
    public void Keyed_registrations_all_forms_are_not_mixed_with_plain_and_count_as_registered()
    {
        // generic keyed, non-generic keyed typeof/typeof, keyed factory: ключ не принимается за тип
        Assert.Empty(Run("""
            public interface IA { } public class A : IA { } public interface IB { } public class B : IB { } public interface IC { }
            public class U { public U(IA a, IB b, IC c) { } }
            class P { void M(IServiceCollection s) {
                s.AddKeyedSingleton<IA, A>("k");
                s.AddKeyedSingleton(typeof(IB), "k", typeof(B));
                s.AddKeyedScoped<IC>("k", (sp, key) => null);
                s.AddScoped<U>();
            } }
            """));
        // keyed Scoped не считается обычной регистрацией: Singleton, зависящий от неё без ключа, — это missing, а не captive
        var all = Run("""
            public interface IA { } public class A : IA { } public class H { public H(IA a) { } }
            class P { void M(IServiceCollection s) { s.AddKeyedScoped<IA, A>("k"); s.AddSingleton<H>(); } }
            """);
        Assert.DoesNotContain(all, p => p.Kind is "di-captive" or "di-lifetime-hint");
    }

    [Fact]
    public void Several_hosted_services_are_all_roots()
    {
        var p = Assert.Single(Run("""
            public class AppDbContext { }
            public class W1 { public W1(AppDbContext d) { } } public class W2 { public W2(AppDbContext d) { } }
            class P { void M(IServiceCollection s) {
                s.AddDbContext<AppDbContext>(); s.AddHostedService<W1>(); s.AddHostedService<W2>();
            } }
            """));
        Assert.Equal(2, p.Items!.Count);
        Assert.Contains(p.Items!, i => i.Name == "W1 → AppDbContext");
        Assert.Contains(p.Items!, i => i.Name == "W2 → AppDbContext");
    }

    [Fact]
    public void No_registrations_at_all_with_controllers_stays_silent()
    {
        Assert.Empty(Run("""
            public interface IOrders { }
            public class OrdersController : ControllerBase { public OrdersController(IOrders o) { } }
            """));
    }

    [Fact]
    public void TryAddEnumerable_open_generic_and_unknown_forms()
    {
        // ServiceDescriptor.Scoped<I,T>() внутри TryAddEnumerable — регистрация I; открытый дженерик закрывает IRepo<X>
        Assert.Empty(Run("""
            public interface IHandler { } public class H1 : IHandler { }
            public interface IRepo<T> { } public class Repo<T> : IRepo<T> { }
            public class U { public U(IHandler h, IRepo<int> r) { } }
            class P { void M(IServiceCollection s) {
                s.TryAddEnumerable(ServiceDescriptor.Scoped<IHandler, H1>());
                s.AddScoped(typeof(IRepo<>), typeof(Repo<>));
                s.AddScoped<U>();
            } }
            """));
        // нераспознанные формы регистрации делают набор неполным → missing только с низкой уверенностью и как покрытие
        foreach (var form in new[] { "s.AddSingleton(new Foo());", "s.Add(new ServiceDescriptor(typeof(IFoo), typeof(Foo), ServiceLifetime.Scoped));",
                                     "s.AddRefitClient(typeof(IFoo));", "s.AddScoped(t);", "s.AddSingleton(ServiceDescriptor.Singleton(typeof(IFoo), typeof(Foo)));" })
        {
            var p = Assert.Single(Run($$"""
                public interface IFoo { } public class Foo : IFoo { } public class Bar { public Bar(IFoo f) { } }
                class P { void M(IServiceCollection s, System.Type t) { s.AddScoped<Bar>(); {{form}} } }
                """));
            Assert.Equal("di-missing", p.Kind);
            Assert.Equal(ProblemSeverity.Coverage, p.Severity);
            Assert.Contains("низкая", Assert.Single(p.Items!).Note);
        }
    }

    [Fact]
    public void Multiple_constructors_lower_confidence_unless_marked_with_activator_attribute()
    {
        var p = Assert.Single(Run("""
            public interface IFoo { } public class Bar { public Bar() { } public Bar(IFoo f) { } }
            class P { void M(IServiceCollection s) { s.AddScoped<Bar>(); } }
            """));
        Assert.Equal(ProblemSeverity.Coverage, p.Severity);                      // не Warning: контейнер мог выбрать другой конструктор
        Assert.Contains("средняя", Assert.Single(p.Items!).Note);

        var marked = Assert.Single(Run("""
            public interface IFoo { } public class Bar { public Bar(int x) { } [ActivatorUtilitiesConstructor] public Bar(IFoo f) { } }
            class P { void M(IServiceCollection s) { s.AddScoped<Bar>(); } }
            """));
        Assert.Equal(ProblemSeverity.Warning, marked.Severity);
        Assert.Contains("высокая", Assert.Single(marked.Items!).Note);
    }

    [Fact]
    public void Findings_per_problem_are_capped_with_full_count_in_title()
    {
        var types = new System.Text.StringBuilder("public class Db { }\n");
        var regs = new System.Text.StringBuilder("class P { void M(IServiceCollection s) { s.AddScoped<Db>();\n");
        const int n = DiLifetimeDetector.MaxItemsPerProblem + 50;
        for (var i = 0; i < n; i++)
        {
            types.Append($"public class H{i} {{ public H{i}(Db d) {{ }} }}\n");
            regs.Append($"s.AddSingleton<H{i}>();\n");
        }
        regs.Append("} }");
        var p = Assert.Single(Run(types + regs.ToString()));
        Assert.Equal(DiLifetimeDetector.MaxItemsPerProblem, p.Items!.Count);
        Assert.Contains($"показано {DiLifetimeDetector.MaxItemsPerProblem} из {n}", p.Title);
    }

    [Fact]
    public void Very_long_call_chain_parses_in_linear_time()
    {
        // 20 000 звеньев AddScoped<T>() в одной цепочке: ToString() узла на каждом звене был бы квадратичным
        var chain = string.Concat(Enumerable.Repeat(".AddScoped<Db>()", 20_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var all = Run($$"""
            public class Db { }
            class P { void M(IServiceCollection s) { s{{chain}}.Scan(x => x.FromAssemblyOf<P>().AddClasses()); } }
            """);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"разбор цепочки занял {sw.Elapsed}");
        Assert.Empty(all);
    }

    [Fact]
    public void Cancellation_is_honoured_inside_a_large_file()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "af_di_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "P.cs"), "class P { void M(IServiceCollection s) { s.AddScoped<Db>(); } }");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => new RoslynDiSource().Read(tmp, cts.Token));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Service_without_di_model_or_with_failed_analysis_is_silent()
    {
        var a = new ServiceAnalysis(new DiscoveredService("x", "/x", null), "sys/x", null);
        Assert.Empty(DiLifetimeDetector.Detect([a]));
    }
}
