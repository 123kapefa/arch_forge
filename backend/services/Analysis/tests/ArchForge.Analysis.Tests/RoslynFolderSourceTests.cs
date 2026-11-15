using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Правило «раскладка по сущности»: методичка «Структура папок» — папки по сущности, а не по тех. признаку; без Manager/Helper/Resolver.
public class RoslynFolderSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_fold_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private void Write(string rel, string code = "class X {}")
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, code);
    }

    private Core.FolderFacts Scan() => new RoslynFolderSource().Read(_root);

    [Fact]
    public void Technical_folders_and_names_are_found_and_entity_layout_is_not()
    {
        Write("src/Svc.Core/Features/Clients/CreateClient.cs");
        Write("src/Svc.Core/Features/Clients/Queries/GetClient.cs");              // Queries внутри Features/{Entity} — правильно
        Write("src/Svc.Core/Features/Clients/UseCases/Rename.cs");
        Write("src/Svc.Domain/Clients/Client.cs");
        Write("src/Svc.Core/Commands/CreateClient.cs");                             // Commands вне Features — тех. папка
        Write("src/Svc.Core/Services/Notifier.cs");
        Write("src/Svc.Infrastructure/Helpers/Dates.cs");
        Write("src/Svc.Core/Common/Result.cs");
        Write("src/Svc.Core/Features/Founders/FounderResolver.cs");
        Write("src/Svc.Core/Features/Clients/ClientManager.cs");
        Write("src/Svc.Core/Features/Clients/Postprocessor.cs");
        Write("src/Svc.Core/TransactionManager.cs");                              // принятая абстракция методички
        Write("src/Svc.Core/ITransactionManager.cs");                             // её интерфейс — тоже
        Write("src/Svc.Core/UserManager.cs");                                     // ASP.NET Identity
        Write("src/Svc.Infrastructure/Services/Smtp.cs");                         // адаптеры в Infrastructure/Services — не тех. бакет
        Write("src/Svc.Core/Features/Queries/GetAll.cs");                         // Queries без сущности — не правильная раскладка
        Write("src/Svc.Core/Features/Clients/Services/Notifier.cs");              // Services внутри сущности — всё равно тех. папка
        Write("src/Svc.Core/Utility/U.cs");                   // слово с маленькой буквы — не считается
        var f = Scan();
        Assert.Equal(["src/Svc.Core/Commands", "src/Svc.Core/Common", "src/Svc.Core/Features/Clients/Services", "src/Svc.Core/Features/Queries", "src/Svc.Core/Services", "src/Svc.Core/Utility", "src/Svc.Infrastructure/Helpers"], f.TechFolders.Select(x => x.Name).ToArray());
        Assert.Equal(["ClientManager", "FounderResolver"], f.TechNames.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Nested_technical_folders_are_reported_once_each_and_tests_contracts_shared_are_skipped()
    {
        Write("src/Svc.Core/Utils/Strings/A.cs");
        Write("src/Svc.Core/Utils/B.cs");
        Write("src/Svc.Tests/Helpers/H.cs");
        Write("src/Svc.Contracts/Common/C.cs");
        Write("src/Shared/Svc.Shared.Kernel/Common/K.cs");
        var f = Scan();
        Assert.Equal(["src/Svc.Core/Utils"], f.TechFolders.Select(x => x.Name).ToArray());
        Assert.Empty(f.TechNames);
    }

    [Fact]
    public void Missing_root_is_empty_and_hostile_names_are_cleaned_and_bounded()
    {
        Assert.Empty(new RoslynFolderSource().Read(Path.Combine(_root, "nope")).TechFolders);
        Write("src/Svc.Core/" + new string('A', 120) + "Manager.cs");
        var n = Assert.Single(Scan().TechNames);
        Assert.True(n.Name.Length <= 201);
    }
}
