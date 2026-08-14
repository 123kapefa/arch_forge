using ArchForge.Analysis.Infrastructure.Manifests;

namespace ArchForge.Analysis.Tests.Systems;

public class InterpolationTests
{
    [Theory]
    [InlineData("${IMAGE_TAG:-stage}", "stage")]
    [InlineData("${VAR:-}", "")]
    [InlineData("prefix-${X:-a}-suffix", "prefix-a-suffix")]
    [InlineData("${REQUIRED:?must be set}", "${REQUIRED}")]
    [InlineData("${PLAIN}", "${PLAIN}")]
    [InlineData("Host=postgres-auth;Database=${DB:-auth_db}", "Host=postgres-auth;Database=auth_db")]
    public void Resolve_handles_compose_var_forms(string input, string expected) =>
        Assert.Equal(expected, Interpolation.Resolve(input));

    [Fact]
    public void IsDeferred_detects_unresolved()
    {
        Assert.True(Interpolation.IsDeferred("${X}"));
        Assert.False(Interpolation.IsDeferred("stage"));
    }
}
