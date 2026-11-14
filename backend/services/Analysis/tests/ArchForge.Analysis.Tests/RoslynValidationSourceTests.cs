using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Правило «двойная валидация»: методичка «Валидация — один раз» — правило формата поля имеет одного владельца.
public class RoslynValidationSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_valid_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private Core.ValidationFacts Scan(string code, string file = "src/Svc.Core/V.cs")
    {
        var p = Path.Combine(_root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, code);
        return new RoslynValidationSource().Read(_root);
    }

    [Fact]
    public void Format_rules_in_validators_and_string_factories_of_value_objects_are_found()
    {
        var f = Scan("""
            public sealed class CreateTagValidator : AbstractValidator<CreateTag>
            {
                public CreateTagValidator()
                {
                    RuleFor(x => x.Request.Title).NotEmpty().MaximumLength(150).Matches("^a");
                    RuleFor(x => x.Email).EmailAddress();
                    RuleFor(x => x.Page).GreaterThan(0);
                    RuleFor(x => x.Comment).NotNull();
                }
            }
            public record TagTitle { public static Result<TagTitle, Error> Of(string value) => null; }
            public readonly struct Inn { public static Inn Parse(string raw) => default; }
            public record TagId { public static TagId Of(Guid id) => null; }
            """);
        var r = f.Rules.OrderBy(x => x.Member, StringComparer.Ordinal).ToList();
        Assert.Equal(["Email", "Request.Title"], r.Select(x => x.Member).ToArray());           // NotNull/GreaterThan — не формат
        Assert.Equal(["MaximumLength", "Matches"], r.Single(x => x.Member == "Request.Title").Checks);
        Assert.All(r, x => Assert.Equal("CreateTagValidator", x.Validator));
        Assert.Equal(["Inn", "TagTitle"], f.ValueObjects.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());   // от Guid — не VO
    }

    [Fact]
    public void Delegation_to_the_value_object_and_non_validators_are_not_reported()
    {
        var f = Scan("""
            class V : AbstractValidator<C>
            {
                V()
                {
                    RuleFor(x => x.Inn).MustBe(Inn.Of);
                    RuleFor(x => x.Title).MaximumLength(10).Must(t => Title.Of(t).IsSuccess);
                    RuleFor(x => x.Name).SetValidator(new NameValidator());
                }
            }
            class NotAValidator { void M() { RuleFor(x => x.Title).MaximumLength(5); } }
            """);
        Assert.Empty(f.Rules);
    }

    [Fact]
    public void Tests_contracts_and_shared_layers_are_skipped_and_hostile_input_is_bounded()
    {
        const string code = "class V : AbstractValidator<C> { V() { RuleFor(x => x.Title).MaximumLength(5); } } record Title { public static Title Of(string s) => null; }";
        Assert.Empty(Scan(code, "tests/Svc.Tests/V.cs").Rules);
        Assert.Empty(Scan(code, "src/Svc.Contracts/V.cs").Rules);
        Assert.Empty(Scan(code, "src/Shared/Svc.Shared.Kernel/V.cs").ValueObjects);
        Assert.Single(Scan(code, "src/Svc.Core/V.cs").Rules);

        File.Delete(Path.Combine(_root, "src/Svc.Core/V.cs"));
        var many = string.Concat(Enumerable.Range(0, 3000).Select(_ => "RuleFor(x => x.Title).MaximumLength(5);\n"));
        var hostile = Scan("class " + new string('A', 5000) + " : AbstractValidator<C> { A() {\n" + many + "} }", "src/Svc.Core/H.cs");
        Assert.InRange(hostile.Rules.Count, 1, 200);                                   // потолок кандидатов на файл
        Assert.All(hostile.Rules, r => Assert.True(r.Validator.Length <= 201));
    }

    [Fact]
    public void Business_checks_do_not_hide_format_rules_and_value_objects_must_return_themselves()
    {
        var f = Scan("""
            class V : AbstractValidator<C>
            {
                V()
                {
                    RuleFor(x => x.Title).MaximumLength(150).MustAsync(NotExist);
                    RuleFor(x => x.Customer.Name!).MinimumLength(2);
                    RuleFor(x => x.Product.Name).MinimumLength(2);
                }
            }
            record Title { public static Result<Title, Error> Of(string? v) => null; }
            class Order { public static Order Create(string name) => null; }
            class Slug { public static Slug Parse(string s) => null; }
            class Helper { public static int Parse(string s) => 0; }
            static class Util { public static Util Create(string s) => null; }
            abstract class Base { public static Base Of(string s) => null; }
            """);
        Assert.Equal(["Customer.Name", "Product.Name", "Title"], f.Rules.Select(x => x.Member).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(["Order", "Slug", "Title"], f.ValueObjects.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());   // Helper, Util, Base — нет
    }
}
