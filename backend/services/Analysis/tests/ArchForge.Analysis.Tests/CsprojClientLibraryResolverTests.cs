using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests;

// Клиентские библиотеки сервиса: ProjectReference вне папки сервиса + вызов метода регистрации клиента сервисом.
public class CsprojClientLibraryResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_libres_" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ } }

    private string Write(string rel, string text)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private static string Csproj(params string[] refs) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" + string.Concat(refs.Select(r => $"<ProjectReference Include=\"{r}\" />")) + "</ItemGroup></Project>";

    private void Library(string name = "Tag", string method = "AddTagServiceHttpCommunication")
    {
        Write($"{name}/src/{name}.Contracts/{name}.Contracts.csproj", Csproj());
        Write($"{name}/src/{name}.Contracts/Ext.cs",
            $"public static class Ext {{ public static IServiceCollection {method}(this IServiceCollection s, IConfiguration c) {{ s.AddHttpClient<ITagClient, TagClient>(); return s; }} }}");
    }

    private IReadOnlyList<Core.ClientLibrary> Resolve(string service) => new CsprojClientLibraryResolver().Resolve(Path.Combine(_root, service), _root);

    [Fact]
    public void Referenced_library_whose_registration_the_service_calls_is_returned()
    {
        Library();
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj"));
        Write("Comment/src/Comment.Core/Reg.cs", "class R { void M(IServiceCollection s, IConfiguration c) { s.AddTagServiceHttpCommunication(c); } }");

        var lib = Assert.Single(Resolve("Comment"));
        Assert.Equal("Tag.Contracts", lib.Name);
        Assert.EndsWith("Tag.Contracts", lib.Dir);
    }

    [Fact]
    public void Referenced_library_without_registration_call_is_not_a_client()
    {
        Library();    // подключена ради DTO: метод регистрации сервис не вызывает
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj"));
        Write("Comment/src/Comment.Core/Use.cs", "class U { Tag.Contracts.Dto d; }");
        Assert.Empty(Resolve("Comment"));
    }

    [Fact]
    public void Registration_call_without_project_reference_is_not_enough()
    {
        Library();
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj());      // ссылки нет
        Write("Comment/src/Comment.Core/Reg.cs", "class R { void M(IServiceCollection s, IConfiguration c) { s.AddTagServiceHttpCommunication(c); } }");
        Assert.Empty(Resolve("Comment"));
    }

    [Fact]
    public void Project_inside_own_folder_and_missing_or_hostile_csproj_are_ignored()
    {
        Library();
        Write("Comment/src/Own/Own.csproj", Csproj());
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\Own\Own.csproj", @"..\..\..\Nothing\Missing.csproj"));
        Write("Comment/src/Broken/Broken.csproj", "<Project><ItemGroup><ProjectReference Include=");                       // битый XML
        Write("Comment/src/Evil/Evil.csproj", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><Project>&e;</Project>");   // DTD запрещён
        Write("Comment/src/Comment.Core/Reg.cs", "class R { void M(IServiceCollection s, IConfiguration c) { s.AddTagServiceHttpCommunication(c); } }");
        Assert.Empty(Resolve("Comment"));
    }

    [Fact]
    public void Two_libraries_only_the_registered_one_counts()
    {
        Library("Tag", "AddTagServiceHttpCommunication");
        Library("File", "AddFileServiceHttpCommunication");
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj", @"..\..\..\File\src\File.Contracts\File.Contracts.csproj"));
        Write("Comment/src/Comment.Core/Reg.cs", "class R { void M(IServiceCollection s, IConfiguration c) { s.AddFileServiceHttpCommunication(c); } }");
        Assert.Equal("File.Contracts", Assert.Single(Resolve("Comment")).Name);
    }

    [Fact]
    public void Method_name_in_comment_or_string_is_not_a_call()
    {
        Library();
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj"));
        Write("Comment/src/Comment.Core/Note.cs", "class N { string s = \"AddTagServiceHttpCommunication\"; /* s.AddTagServiceHttpCommunication(c); */ }");
        Assert.Empty(Resolve("Comment"));
    }

    [Fact]
    public void Nonexistent_root_and_library_without_registration_methods_give_nothing()
    {
        Assert.Empty(new CsprojClientLibraryResolver().Resolve(Path.Combine(_root, "nope"), _root));
        Write("Plain/src/Plain.Contracts/Plain.Contracts.csproj", Csproj());
        Write("Plain/src/Plain.Contracts/Dto.cs", "public class Dto { }");
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Plain\src\Plain.Contracts\Plain.Contracts.csproj"));
        Assert.Empty(Resolve("Comment"));
    }

    // ── замечания ревью и security-ревью ──

    [Fact]
    public void Library_outside_the_system_root_is_never_read()
    {
        // враждебный csproj: ссылка на проект вне корня анализа (даже существующий) и вызов метода с тем же именем
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "archforge_outside_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "Foreign.Client.csproj"), Csproj());
            File.WriteAllText(Path.Combine(outside, "Ext.cs"), "public static class E { public static IServiceCollection AddForeign(this IServiceCollection s) { s.AddHttpClient<A>(); return s; } }");
            Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(outside.Replace('/', '\\') + "\\Foreign.Client.csproj", "../../../../" + Path.GetFileName(outside) + "/Foreign.Client.csproj"));
            Write("Comment/src/Comment.Core/Reg.cs", "class R { void M(IServiceCollection s) { s.AddForeign(); } }");
            Assert.Empty(Resolve("Comment"));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Only_client_named_libraries_count_not_shared_ones_like_authentication()
    {
        Write("Shared/Authentication/Authentication.csproj", Csproj());
        Write("Shared/Authentication/Ext.cs", "public static class E { public static IServiceCollection AddAuthClient(this IServiceCollection s) { s.AddHttpClient<AuthApi>(); return s; } }");
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Shared\Authentication\Authentication.csproj"));
        Write("Comment/src/Comment.Core/Reg.cs", "class R { void M(IServiceCollection s) { s.AddAuthClient(); } }");
        Assert.Empty(Resolve("Comment"));
    }

    [Fact]
    public void Same_method_name_declared_by_the_service_or_in_two_libraries_is_not_counted()
    {
        Library("Tag", "AddClients");
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj"));
        Write("Comment/src/Comment.Core/Reg.cs", "static class Mine { public static IServiceCollection AddClients(this IServiceCollection s) => s; } class R { void M(IServiceCollection s) { s.AddClients(); } }");
        Assert.Empty(Resolve("Comment"));                              // своё AddClients: чужую библиотеку не притягиваем

        Library("File", "AddClients");                                 // тот же метод в двух библиотеках
        Write("Order/src/Order.Core/Order.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj", @"..\..\..\File\src\File.Contracts\File.Contracts.csproj"));
        Write("Order/src/Order.Core/Reg.cs", "class R { void M(IServiceCollection s) { s.AddClients(); } }");
        Assert.Empty(Resolve("Order"));
    }

    [Fact]
    public void Only_extension_methods_on_service_collection_are_registrations()
    {
        Write("Tag/src/Tag.Contracts/Tag.Contracts.csproj", Csproj());
        Write("Tag/src/Tag.Contracts/Ext.cs", "public static class E { public static void AddThing(string notServices) { new C().AddHttpClient<A>(); } }");
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj"));
        Write("Comment/src/Comment.Core/Reg.cs", "class R { void M() { E.AddThing(\"x\"); } }");
        Assert.Empty(Resolve("Comment"));
    }

    [Fact]
    public void Two_consumers_of_one_library_both_get_it()
    {
        Library();
        foreach (var c in new[] { "Comment", "Order" })
        {
            Write($"{c}/src/{c}.Core/{c}.Core.csproj", Csproj(@"..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj"));
            Write($"{c}/src/{c}.Core/Reg.cs", "class R { void M(IServiceCollection s, IConfiguration c) { s.AddTagServiceHttpCommunication(c); } }");
        }
        Assert.Single(Resolve("Comment"));
        Assert.Single(Resolve("Order"));        // второй разбор — из кеша методов регистрации
    }

    [Fact]
    public void Noise_directories_are_not_searched_for_projects()
    {
        Library();
        Write("Comment/node_modules/x/Hidden.csproj", Csproj(@"..\..\..\..\Tag\src\Tag.Contracts\Tag.Contracts.csproj"));
        Write("Comment/src/Comment.Core/Comment.Core.csproj", Csproj());
        Write("Comment/src/Comment.Core/Reg.cs", "class R { void M(IServiceCollection s, IConfiguration c) { s.AddTagServiceHttpCommunication(c); } }");
        Assert.Empty(Resolve("Comment"));
    }
}
