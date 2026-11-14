using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Правило «лишняя проверка на null»: методичка «Стратегия ошибок и нулей», правило 3 — на ненулевом параметре внутри сервиса
// ArgumentNullException.ThrowIfNull избыточен (за нули отвечает Nullable); граница API проверяется один раз в валидаторе.
public class RoslynGuardSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_guard_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private IReadOnlyList<Core.NullGuardSite> Scan(string code, string file = "src/Svc.Core/Handler.cs")
    {
        Write(file, code);
        return new RoslynGuardSource().Read(_root);
    }

    [Fact]
    public void Three_guard_forms_on_non_nullable_parameters_are_found()
    {
        var sites = Scan("""
            #nullable enable
            class Handler {
                private readonly string _name;
                public Handler(string name, IFoo foo, IBar bar) {
                    _name = name ?? throw new ArgumentNullException(nameof(name));
                    ArgumentNullException.ThrowIfNull(foo);
                    if (bar is null) throw new ArgumentNullException(nameof(bar));
                }
                public void Rename(WidgetName name) { if (name == null) { throw new ArgumentNullException(nameof(name)); } }
            }
            """);

        Assert.Equal(4, sites.Count);
        Assert.Contains(sites, s => s.Param == "name" && s.Form == "??-throw" && s.Member == "Handler");
        Assert.Contains(sites, s => s.Param == "foo" && s.Form == "ThrowIfNull");
        Assert.Contains(sites, s => s.Param == "bar" && s.Form == "if-throw");
        Assert.Contains(sites, s => s.Param == "name" && s.Member == "Rename" && s.Form == "if-throw");
        Assert.All(sites, s => { Assert.Equal("Handler", s.Type); Assert.True(s.Line > 0); Assert.EndsWith("Handler.cs", s.File); });
    }

    [Fact]
    public void Primary_constructor_and_extension_method_parameters_are_found()
    {
        var sites = Scan("""
            #nullable enable
            class H(IFoo foo) { void M() { ArgumentNullException.ThrowIfNull(foo); } }
            static class Ext { public static IServiceCollection AddX(this IServiceCollection services) { ArgumentNullException.ThrowIfNull(services); return services; } }
            """);
        Assert.Equal(2, sites.Count);
        Assert.Contains(sites, s => s.Type == "H" && s.Param == "foo");
        Assert.Contains(sites, s => s.Type == "Ext" && s.Member == "AddX" && s.Param == "services");
    }

    [Fact]
    public void Nullable_parameters_defaults_generics_and_non_parameters_are_not_flagged()
    {
        Assert.Empty(Scan("""
            #nullable enable
            class H<TItem> {
                private IFoo _f = null!;
                public IFoo? Prop { get; set; }
                void A(string? maybe) { ArgumentNullException.ThrowIfNull(maybe); }                  // тип допускает null
                void B(IFoo foo = null) { ArgumentNullException.ThrowIfNull(foo); }                  // значение по умолчанию null
                void C<T>(T value) { ArgumentNullException.ThrowIfNull(value); }                     // generic может быть ссылочным
                void D(TItem item) { ArgumentNullException.ThrowIfNull(item); }                      // generic класса
                void E() { ArgumentNullException.ThrowIfNull(_f); ArgumentNullException.ThrowIfNull(Prop); var l = Make(); ArgumentNullException.ThrowIfNull(l); }
                void F(Order order) { ArgumentNullException.ThrowIfNull(order.Customer); }           // не сам параметр
                void G(IFoo foo) { Action<IFoo?> a = foo => ArgumentNullException.ThrowIfNull(foo); } // параметр лямбды, а не метода
                IFoo Make() => null!;
            }
            """));
    }

    [Fact]
    public void Without_enabled_nullable_the_check_is_meaningful_and_not_flagged()
    {
        // нет #nullable enable и csproj не включает Nullable — для компилятора ссылка «может быть null», проверка осмысленна
        Assert.Empty(Scan("class H { void A(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); } }"));
        Write("src/Svc.Core/Svc.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Nullable>disable</Nullable></PropertyGroup></Project>");
        Assert.Empty(new RoslynGuardSource().Read(_root));
    }

    [Fact]
    public void Nullable_enabled_in_csproj_or_directory_build_props_counts()
    {
        Write("src/Svc.Core/Svc.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
        Assert.Single(Scan("class H { void A(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); } }"));

        Write("Directory.Build.props", "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
        Write("src/Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Equal(2, Scan("class G { void A(IBar bar) { ArgumentNullException.ThrowIfNull(bar); } }", "src/Other/G.cs").Count);
    }

    [Fact]
    public void Nullable_disable_directive_in_file_wins_and_test_projects_are_skipped()
    {
        Assert.Empty(Scan("#nullable disable\nclass H { void A(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); } }"));
        Assert.Empty(Scan("#nullable enable\nclass T { void A(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); } }", "tests/Svc.Tests/T.cs"));
    }

    [Fact]
    public void Nested_types_and_local_functions_report_the_right_owner()
    {
        var sites = Scan("""
            #nullable enable
            class Outer {
                class Inner { void M(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); } }
                void N() { void Local(IBar bar) { ArgumentNullException.ThrowIfNull(bar); } Local(null!); }
            }
            """);
        Assert.Equal(2, sites.Count);
        Assert.Contains(sites, s => s.Type == "Inner" && s.Member == "M");
        Assert.Contains(sites, s => s.Type == "Outer" && s.Param == "bar");
    }

    // ── замечания ревью ──

    [Fact]
    public void Nullable_directives_are_applied_by_position_like_the_compiler()
    {
        var sites = Scan("""
            class H {
                void Before(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }       // до директивы nullable выключен (проект не включает)
            #nullable enable
                void During(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }
            #nullable disable
                void Off(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }
            #nullable enable warnings
                void WarningsOnly(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); } // warnings аннотации не включает
            #nullable restore
                void Restored(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }     // restore → настройка проекта (выключено)
            #nullable enable annotations
                void Annotations(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }
            }
            """);
        Assert.Equal(["Annotations", "During"], sites.Select(s => s.Member).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Project_setting_reads_real_xml_ignores_comments_and_conditions_and_supports_targets()
    {
        Write("src/Svc.Core/Svc.Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><!-- <Nullable>enable</Nullable> --><PropertyGroup Condition="'$(X)'=='1'"><Nullable>enable</Nullable></PropertyGroup></Project>""");
        Assert.Empty(Scan("class H { void A(IFoo foo) { ArgumentNullException.ThrowIfNull(foo); } }"));          // комментарий и условие не считаются

        Write("src/Svc.Core/Svc.Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Nullable>annotations</Nullable></PropertyGroup></Project>""");
        Assert.Single(new RoslynGuardSource().Read(_root));                                                       // annotations включает аннотации

        File.Delete(Path.Combine(_root, "src/Svc.Core/Svc.Core.csproj"));
        Write("src/Svc.Core/Svc.Core.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write("src/Svc.Core/Directory.Build.targets", "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
        Assert.Single(new RoslynGuardSource().Read(_root));
    }

    [Fact]
    public void Allow_null_attributes_modifiers_value_types_and_nullable_of_t_are_not_flagged()
    {
        Assert.Empty(Scan("""
            #nullable enable
            class H {
                void A([AllowNull] IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }
                void B([System.Diagnostics.CodeAnalysis.MaybeNull] IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }
                void C(ref IFoo foo) { ArgumentNullException.ThrowIfNull(foo); }
                void D(out IFoo foo) { foo = null!; ArgumentNullException.ThrowIfNull(foo); }
                void E(int n, Guid id, DateTime at, CancellationToken ct, bool b) { ArgumentNullException.ThrowIfNull(n); ArgumentNullException.ThrowIfNull(id); }
                void F(Nullable<int> n) { ArgumentNullException.ThrowIfNull(n); }
            }
            """));
        // string и object — ссылочные, проверка лишняя
        Assert.Equal(2, Scan("""
            #nullable enable
            class H { void A(string s, object o) { ArgumentNullException.ThrowIfNull(s); ArgumentNullException.ThrowIfNull(o); } }
            """).Count);
    }

    [Fact]
    public void Record_primary_constructor_parameters_are_properties_and_shadowed_class_members_are_skipped()
    {
        Assert.Empty(Scan("""
            #nullable enable
            record R(IFoo Foo) { void M() { ArgumentNullException.ThrowIfNull(Foo); } }
            class C(IBar bar) { private IBar bar = bar; void M() { ArgumentNullException.ThrowIfNull(bar); } }       // поле перекрывает параметр
            """));
    }

    [Fact]
    public void Public_library_layers_contracts_and_shared_are_not_judged()
    {
        const string code = "#nullable enable\nclass Ext { public static IServiceCollection AddX(this IServiceCollection s) { ArgumentNullException.ThrowIfNull(s); return s; } }";
        Assert.Empty(Scan(code, "src/Svc.Contracts/Ext.cs"));
        Assert.Empty(Scan(code, "src/Shared/Svc.Shared.Kernel/Ext.cs"));
        Assert.Single(Scan(code, "src/Svc.Core/Ext.cs"));                           // внутри сервиса — судим
    }
}
