using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Правило «смесь стратегий ошибок»: методичка «Стратегия ошибок и нулей», правило 1 — в сервисе либо Result, либо исключения.
public class RoslynErrorStrategySourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_errs_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private Core.ErrorStrategyFacts Scan(string code, string file = "src/Svc.Core/Handler.cs")
    {
        var p = Path.Combine(_root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, code);
        return new RoslynErrorStrategySource().Read(_root);
    }

    [Fact]
    public void Result_returns_and_expected_throws_are_both_found()
    {
        var f = Scan("""
            class OrderHandler {
                public Task<Result<Order>> Get(Guid id) => null;
                public Result Cancel(Guid id) => null;
                public ValueTask<Result<int, Error>> Count() => default;
                public Order Find(Guid id) { if (id == default) throw new NotFoundException("order"); return null; }
                public void Check(int n) { var x = n > 0 ? 1 : throw new ValidationException("n"); }
            }
            """);
        Assert.Equal(["Cancel", "Count", "Get"], f.ResultReturns.Select(s => s.Member).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(["Check", "Find"], f.ExpectedThrows.Select(s => s.Member).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(f.ExpectedThrows, s => Assert.Equal("OrderHandler", s.Type));
    }

    [Fact]
    public void Programmer_errors_and_unrelated_exceptions_are_not_expected_errors()
    {
        var f = Scan("""
            class H {
                void A(int x) { if (x < 0) throw new ArgumentOutOfRangeException(nameof(x)); }
                void B() { throw new InvalidOperationException(); }
                void C() { throw new UnreachableException(); }
                void D() { try { } catch { throw; } }
                void E() { throw new NotSupportedException(); }
            }
            """);
        Assert.Empty(f.ExpectedThrows);
    }

    [Fact]
    public void Bridges_are_not_a_mix_constructors_accessors_pipeline_middleware_converters()
    {
        var f = Scan("""
            class Order {
                public Order(string n) { if (n == "") throw new ValidationException("n"); }
                public string Name { get => _n; set { if (value == "") throw new ValidationException("n"); _n = value; } }
                private string _n = "";
                public static implicit operator int(Order o) => throw new ConflictException("x");
            }
            class ValidationBehavior { public Task Handle() => throw new ValidationException("v"); }
            class ErrorMiddleware { public Task Invoke() => throw new NotFoundException("m"); }
            class ApiExceptionHandler { public bool Try() => throw new ConflictException("e"); }
            class PriceConverter { public object Convert() => throw new ValidationException("c"); }
            class CreateOrderHandler { public void Handle() { throw new ConflictException("dup"); } }
            """);
        var s = Assert.Single(f.ExpectedThrows);                                   // обычный хендлер команды — не мост
        Assert.Equal("CreateOrderHandler", s.Type);
        Assert.Equal("ConflictException", s.Name);
    }

    [Fact]
    public void Tests_layer_is_skipped_and_hostile_input_is_bounded()
    {
        var f = Scan("class T { Result A() => null; void B() { throw new NotFoundException(); } }", "tests/Svc.Tests/T.cs");
        Assert.Empty(f.ResultReturns);
        Assert.Empty(f.ExpectedThrows);

        var long_ = new string('A', 5000);
        var many = string.Concat(Enumerable.Range(0, 6000).Select(i => "void M" + i + "() { throw new NotFoundException(); }\n"));
        var g = Scan("class " + long_ + " {\n" + many + "}");
        Assert.InRange(g.ExpectedThrows.Count, 1, 5000);                           // потолок на тип результата
        Assert.All(g.ExpectedThrows, s => Assert.True(s.Type.Length <= 201));
    }

    [Fact]
    public void Interfaces_own_result_type_one_entry_per_method_and_nested_bridges_are_not_a_mix()
    {
        var f = Scan("""
            interface IOrders { Task<Result<Order>> Get(Guid id); }
            class Result<T> { public static Result<T> Success() => null; public static Result<T> Failure() => null; }
            class OrderService : IOrders {
                public Task<Result<Order>> Get(Guid id) => null;
                void A() { if (x) throw new NotFoundException(); if (y) throw new ConflictException(); void Local() { throw new ValidationException(); } }
                public int P => throw new NotFoundException();
            }
            class ErrorMiddleware { class Inner { void B() { throw new NotFoundException(); } } }
            """);
        Assert.Equal(["Get"], f.ResultReturns.Select(s => s.Member).ToArray());            // интерфейс и фабрики Result не считаются
        var t = Assert.Single(f.ExpectedThrows);                                            // три броска в A — один метод; свойство и вложенный мост — нет
        Assert.Equal("OrderService.A", t.Type + "." + t.Member);
    }

    [Fact]
    public void Public_library_layers_contracts_and_shared_are_not_judged()
    {
        const string code = "class E { Result A() => null; void B() { throw new NotFoundException(); } }";
        Assert.Empty(Scan(code, "src/Svc.Contracts/E.cs").ExpectedThrows);
        Assert.Empty(Scan(code, "src/Shared/Svc.Shared.Kernel/E.cs").ResultReturns);
        Assert.Single(Scan(code, "src/Svc.Core/E.cs").ExpectedThrows);
    }

    [Fact]
    public void Rejected_candidates_also_spend_the_per_file_work_budget()
    {
        var body = string.Concat(Enumerable.Range(0, 5000).Select(_ => "throw new NotFoundException();\n"));
        var f = Scan("class XBehavior { void M() {\n" + body + "} }");              // все — мост, но подъёмов не больше потолка
        Assert.Empty(f.ExpectedThrows);
    }
}
