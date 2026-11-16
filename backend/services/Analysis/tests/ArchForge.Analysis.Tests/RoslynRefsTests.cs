using System.Text;
using ArchForge.Analysis.Core;
using ArchForge.Analysis.Core.Systems;
using ArchForge.Analysis.Domain;
using ArchForge.Analysis.Infrastructure.Roslyn;
using Xunit;

namespace ArchForge.Analysis.Tests;

// Слабый вид ребра «refs» (возвращаемые типы, аргументы базы/интерфейсов, статика, тело метода, атрибуты).
public class RoslynRefsTests
{
    private const string Src = """
        using System; using System.Collections.Generic; using System.Threading.Tasks;
        public class Dto { }
        public class Res<T> { }
        public class Base<T> { }
        public interface IMarker<T> { }
        public class ArgOfBase { }
        public class ArgOfIface { }
        public class Consts { public const int X = 1; public static int Y = 2; }
        public class Created { }
        public class Local { public static Local Make() => new(); }
        public class InGeneric { }
        public class StaticHeld { }
        public class Unused { }
        public class RetOnly { public Task<Res<IReadOnlyList<Dto>>> Get() => null; }
        public class Derived : Base<ArgOfBase>, IMarker<ArgOfIface> { }
        public class Body
        {
            public static StaticHeld Held;
            public int Use() { var c = new Created(); var l = Local.Make(); Gen<InGeneric>(); return Consts.X; }
            private static void Gen<T>() { }
        }
        public class Mixed { private readonly Dto _d; public Dto Get() => _d; }
        """;

    private static IReadOnlyList<RawType> Read(string src, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "archforge_refs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "A.cs"), src);
            return new RoslynSemanticTypeSource(new RoslynTypeSource()).Read(tmp, ct);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static IEnumerable<string> RefsOf(IReadOnlyList<RawType> raw, string type, string kind) =>
        raw.First(t => t.Name == type).Refs.Where(r => r.Kind == kind).Select(r => r.Name);

    [Fact]
    public void Return_type_incl_nested_generics_is_refs()
    {
        var raw = Read(Src);
        Assert.Contains("Dto", RefsOf(raw, "RetOnly", "refs"));
        Assert.Contains("Res", RefsOf(raw, "RetOnly", "refs"));
    }

    [Fact]
    public void Base_and_interface_type_arguments_are_refs()
    {
        var raw = Read(Src);
        Assert.Contains("ArgOfBase", RefsOf(raw, "Derived", "refs"));
        Assert.Contains("ArgOfIface", RefsOf(raw, "Derived", "refs"));
    }

    [Fact]
    public void Static_members_and_static_access_and_body_are_refs()
    {
        var raw = Read(Src);
        var refs = RefsOf(raw, "Body", "refs").ToList();
        Assert.Contains("StaticHeld", refs); // статическое поле
        Assert.Contains("Consts", refs);     // Consts.X
        Assert.Contains("Created", refs);    // new Created()
        Assert.Contains("Local", refs);      // Local.Make() / var l
        Assert.Contains("InGeneric", refs);  // аргумент дженерик-вызова
    }

    [Fact]
    public void Negative_unreferenced_type_has_no_incoming_and_stronger_kind_wins()
    {
        var raw = Read(Src);
        Assert.DoesNotContain(raw.SelectMany(t => t.Refs), r => r.Name == "Unused");
        // Dto держится полем (holds) и возвращается (refs) → одно ребро, сильное
        var mixed = raw.First(t => t.Name == "Mixed").Refs.Where(r => r.Name == "Dto").ToList();
        Assert.Single(mixed);
        Assert.Equal("holds", mixed[0].Kind);
    }

    [Fact]
    public void Attributes_are_refs()
    {
        var raw = Read("using System; public class MyAttr : Attribute { } [MyAttr] public class Tagged { }");
        Assert.Contains("MyAttr", RefsOf(raw, "Tagged", "refs"));
    }

    [Fact]
    public void Body_limit_truncates_without_failing()
    {
        var sb = new StringBuilder("public class Early { } public class Late { } public class Big { public void M() {\n");
        sb.AppendLine("var e = new Early();");
        for (var i = 0; i < RoslynSemanticTypeSource.MaxBodyNodesPerType; i++) sb.AppendLine("var x" + i + " = 1;");
        sb.AppendLine("var l = new Late(); } }");
        var raw = Read(sb.ToString());
        var refs = RefsOf(raw, "Big", "refs").ToList();
        Assert.Contains("Early", refs);
        Assert.DoesNotContain("Late", refs); // за лимитом узлов — усечено (warning в логе)
    }

    [Fact]
    public void Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read(Src, cts.Token));
    }

    [Fact]
    public void Floor_adds_weak_refs_by_name_but_keeps_stronger_kinds()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "archforge_refs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "A.cs"), "public class Dto { } public class Svc(Dto d) { public Dto Get() => new Dto(); } public class Other { public void M() { var d = Dto.Make(); } }");
            var res = new TypeClassifier().Classify(new RoslynTypeSource().Read(tmp), ConventionPack.Default());
            Assert.Contains(res.Edges, e => e is { From: "Svc", To: "Dto", Kind: "uses" });
            Assert.DoesNotContain(res.Edges, e => e is { From: "Svc", To: "Dto", Kind: "refs" }); // сильное уже есть
            Assert.Contains(res.Edges, e => e is { From: "Other", To: "Dto", Kind: "refs" });
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Weak_refs_do_not_make_layer_violations()
    {
        TypeInfo T(string n, string layer) => new(n, "class", layer, "", "unknown", [], "", [], []);
        var types = new[] { T("Dom", "Domain"), T("Inf", "Infrastructure") };
        var layerOf = LayerGraph.LayerByName(types);
        Assert.Empty(LayerGraph.Violations([new TypeEdge("Dom", "Inf", "refs")], layerOf));
        Assert.Single(LayerGraph.Violations([new TypeEdge("Dom", "Inf", "uses")], layerOf));
    }
}
