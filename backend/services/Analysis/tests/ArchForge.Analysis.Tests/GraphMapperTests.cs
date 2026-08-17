using ArchForge.Analysis.Core;
using ArchForge.Analysis.Domain;

namespace ArchForge.Analysis.Tests;

public class GraphMapperTests
{
    private static TypeInfo Type(string name, string layer, string file) =>
        new(name, "class", layer, "unknown", "unknown", [], file, [], []);

    // id узла — React-ключ карточки L2; одноимённые типы в слое обязаны получить РАЗНЫЕ id (иначе одна пропадёт).
    [Fact]
    public void Duplicate_type_names_in_layer_get_unique_ids()
    {
        var result = new AnalysisResult(
            [
                Type("ApiResponse", "Shared", "SharedKernel/ApiResponse.cs"),
                Type("ApiResponse", "Shared", "AspNetCore/ApiResponse.cs"),
                Type("Order", "Domain", "Order.cs"),
            ],
            [], new Coverage(0, 0, 3), []);

        var ids = GraphMapper.ToDto(result, "proj").Nodes.Select(n => n.Id).ToList();
        Assert.Equal(3, ids.Count);                          // ни один узел не потерян
        Assert.Equal(ids.Count, ids.Distinct().Count());     // все id уникальны
        Assert.Contains("Shared:ApiResponse", ids);          // первый экземпляр — обычный формат
    }

    // без коллизии id остаётся привычным «{Слой}:{Имя}»
    [Fact]
    public void Unique_name_keeps_layer_name_id()
    {
        var result = new AnalysisResult([Type("Order", "Domain", "Order.cs")], [], new Coverage(0, 0, 1), []);
        Assert.Equal("Domain:Order", GraphMapper.ToDto(result, "p").Nodes[0].Id);
    }

    // id члена — тоже React-ключ; перегрузки (два «Fail») обязаны получить разные id
    [Fact]
    public void Overloaded_members_get_unique_ids()
    {
        var t = new TypeInfo("ApiResponse", "class", "Shared", "unknown", "unknown", [], "ApiResponse.cs", [],
            [new MemberInfo("Fail", "method", "(string e)"), new MemberInfo("Fail", "method", "(Error e)")]);
        var members = GraphMapper.ToDto(new AnalysisResult([t], [], new Coverage(0, 0, 1), []), "p").Nodes[0].Members!;
        var ids = members.Select(m => m.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
