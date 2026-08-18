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
}
