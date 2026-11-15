using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Правило «время и аудит в агрегате»: методичка «Агрегаты, VO и доменные события» — агрегат часов не читает, аудит ставит агрегат.
public class RoslynAggregateTimeSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_time_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private Core.AggregateTimeFacts Scan(string code, string file = "src/Svc.Domain/Widget.cs")
    {
        var p = Path.Combine(_root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, code);
        return new RoslynAggregateTimeSource().Read(_root);
    }

    [Fact]
    public void Clock_reads_in_the_domain_layer_are_found_in_all_forms()
    {
        var f = Scan("""
            class Widget {
                public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
                public Widget() { CreatedAt = DateTime.UtcNow; }
                public void Rename() { UpdatedAt = System.DateTimeOffset.Now; var t = DateTime.Today; }
                public void Ok(DateTime now) { UpdatedAt = now; var x = clock.Now; }
            }
            """);
        Assert.Equal(["DateTime.Today", "DateTime.UtcNow", "DateTime.UtcNow", "DateTimeOffset.Now"], f.DomainClock.Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Contains(f.DomainClock, s => s.Member == "Rename" && s.Type == "Widget");
        Assert.Empty(f.InterceptorAudit);
    }

    [Fact]
    public void Clock_outside_the_domain_is_not_this_rule_and_tests_contracts_shared_are_skipped()
    {
        const string code = "class H { void M() { var t = DateTime.UtcNow; } }";
        Assert.Empty(Scan(code, "src/Svc.Core/H.cs").DomainClock);                  // хендлер берёт время — как раз по методичке
        Assert.Empty(Scan(code, "src/Svc.Domain.Tests/H.cs").DomainClock);
        Assert.Empty(Scan(code, "src/Shared/Svc.Shared.Kernel/H.cs").DomainClock);
        Assert.Single(Scan(code, "src/Svc.Domain/H.cs").DomainClock);
    }

    [Fact]
    public void Audit_fields_set_by_interceptor_or_save_changes_are_found_but_plain_mapping_is_not()
    {
        var f = Scan("""
            class AuditInterceptor : SaveChangesInterceptor {
                public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> r, CancellationToken ct) {
                    foreach (var entry in e.Context.ChangeTracker.Entries()) {
                        entry.Entity.UpdatedAt = DateTime.UtcNow;
                        entry.Property("CreatedAt").CurrentValue = DateTime.UtcNow;
                        other = 1;
                    }
                    return default;
                }
            }
            class AppDb : DbContext { public override int SaveChanges() { x.ModifiedAt = DateTime.UtcNow; return 0; } }
            class Mapper { Dto Map(Order o) { return new Dto { CreatedAt = o.CreatedAt }; } void Fill(Dto d) { d.CreatedAt = DateTime.UtcNow; } }
            class Repo { void Save() { SaveChanges(); x.UpdatedAt = DateTime.UtcNow; } void SaveChanges() { } }
            """, "src/Svc.Infrastructure/Audit.cs");
        Assert.Equal(["CreatedAt", "ModifiedAt", "UpdatedAt"], f.InterceptorAudit.Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Empty(f.DomainClock);
    }

    [Fact]
    public void Object_initializers_and_dto_assignments_do_not_use_up_the_audit_budget()
    {
        var dto = string.Concat(Enumerable.Range(0, 300).Select(i => "var d" + i + " = new Dto { CreatedAt = x };\n"));
        var f = Scan("class AppDb : DbContext {\n void Seed() {\n" + dto + "}\n public override int SaveChanges() { e.UpdatedAt = DateTime.UtcNow; return 0; } }", "src/Svc.Infrastructure/Db.cs");
        var a = Assert.Single(f.InterceptorAudit);
        Assert.Equal("UpdatedAt", a.Name);
    }

    [Fact]
    public void Hostile_input_is_bounded()
    {
        var many = string.Concat(Enumerable.Range(0, 3000).Select(_ => "var t = DateTime.UtcNow;\n"));
        var f = Scan("class " + new string('A', 5000) + " { void M() {\n" + many + "} }");
        Assert.InRange(f.DomainClock.Count, 1, 200);                                 // потолок кандидатов на файл
        Assert.All(f.DomainClock, s => Assert.True(s.Type.Length <= 201));
    }
}
