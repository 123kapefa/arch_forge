using ArchForge.Analysis.Infrastructure.Roslyn;

namespace ArchForge.Analysis.Tests.Systems;

// Слой типа определяется по пути (имени проекта). Проверяем распознавание сквозного shared kernel
// (Kalmykov.Shared.*) как отдельного слоя «Shared», не задевая слои конкретного сервиса.
public class RoslynScanLayerTests
{
    [Theory]
    // shared kernel (Kalmykov.Shared.*: SharedKernel/AspNetCore/…) → слой Shared, а не unknown
    [InlineData("backend/src/Shared/Kalmykov.Shared.SharedKernel/Error.cs", "Shared")]
    [InlineData("backend/src/Shared/Kalmykov.Shared.AspNetCore/ExceptionHandling.cs", "Shared")]
    // Shared важнее .Contracts: общий контракт платформы — часть shared kernel
    [InlineData("backend/src/Shared/Kalmykov.Shared.Contracts/Envelope.cs", "Shared")]
    // тест внутри shared остаётся tests (Test проверяется раньше Shared)
    [InlineData("backend/tests/Shared/Testing.Common/FakeHttpMessageHandler.cs", "tests")]
    // слои конкретного сервиса не задеты
    [InlineData("backend/src/Services/Catalog/Kalmykov.Catalog.Domain/Product.cs", "Domain")]
    [InlineData("backend/src/Services/Catalog/Kalmykov.Catalog.Application/CreateWarehouseCommand.cs", "Application")]
    [InlineData("backend/src/Services/Catalog/Kalmykov.Catalog.Contracts/Dto.cs", "Contracts")]
    // внутрисервисная папка Shared — НЕ shared kernel (нет dotted-сегмента .Shared. в имени проекта)
    [InlineData("backend/src/Services/Catalog/Kalmykov.Catalog.Application/Shared/Helpers.cs", "Application")]
    public void Layer_classifies_shared_kernel(string path, string expected) =>
        Assert.Equal(expected, RoslynScan.Layer(path));

    // Слой не зависит от имён папок ВЫШЕ корня сканирования: «archforge-testbed» содержит «test», но это не слой tests.
    [Theory]
    [InlineData("/Users/me/archforge-testbed/svc/src/Svc.Domain/A.cs", "/Users/me/archforge-testbed/svc", "Domain")]
    [InlineData("/Users/me/archforge-testbed/svc/src/Svc.Core/A.cs", "/Users/me/archforge-testbed/svc", "Application")]
    [InlineData("/Users/me/archforge-testbed/svc/src/Svc.Web/P.cs", "/Users/me/archforge-testbed/svc", "Api")]
    [InlineData("/home/ci/latest/svc/src/Svc.Infrastructure.Postgres/R.cs", "/home/ci/latest/svc", "Infrastructure")]
    // настоящие тесты внутри корня остаются tests
    [InlineData("/Users/me/archforge-testbed/svc/tests/Svc.IntegrationTests/T.cs", "/Users/me/archforge-testbed/svc", "tests")]
    [InlineData("/Users/me/x/svc/src/Svc.Domain/Tests/Helper.cs", "/Users/me/x/svc", "tests")]
    // корнем открыта сама папка проекта: её имя сохраняется в пути
    [InlineData("/Users/me/archforge-testbed/svc/src/Svc.Domain/Entities/A.cs", "/Users/me/archforge-testbed/svc/src/Svc.Domain", "Domain")]
    public void Layer_ignores_folder_names_above_the_scan_root(string file, string root, string expected) =>
        Assert.Equal(expected, RoslynScan.Layer(file, root));

    [Fact]
    public void File_outside_root_falls_back_to_plain_path_classification() =>
        Assert.Equal("Domain", RoslynScan.Layer("/elsewhere/Svc.Domain/A.cs", "/Users/me/root"));

    [Fact]
    public void Root_with_trailing_separator_is_equivalent() =>
        Assert.Equal("Domain", RoslynScan.Layer("/Users/me/archforge-testbed/svc/src/Svc.Domain/A.cs", "/Users/me/archforge-testbed/svc/"));
}
