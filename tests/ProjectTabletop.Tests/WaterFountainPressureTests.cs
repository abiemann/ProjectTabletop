using System.Security.Cryptography;
using ProjectTabletop.App.Projection.WaterGarden;

namespace ProjectTabletop.Tests;

public sealed class WaterFountainPressureTests
{
    [Theory]
    [InlineData(38)]
    [InlineData(60)]
    public void GroupedUpdatesPreserveParticleFlowAndDrainage(int framesPerSecond)
    {
        var fixedSteps = new WaterFountainFluid(512f / 404);
        var groupedSteps = new WaterFountainFluid(512f / 404);
        long fixedImpacts = 0, groupedImpacts = 0;

        // Exercise source startup, new particles, settled neighbor pressure,
        // particle removal and the remaining water after switching the source off.
        for (int second = 0; second < 6; second++)
        {
            bool sourceEnabled = second < 4;
            for (int step = 0; step < 120; step++)
            {
                fixedSteps.Advance(WaterFountainFluid.FixedStep, sourceEnabled);
                fixedImpacts += fixedSteps.DrainImpacts().Sum(impact => impact.Particles);
            }
            for (int frame = 0; frame < framesPerSecond; frame++)
            {
                groupedSteps.Advance(1.0 / framesPerSecond, sourceEnabled);
                groupedImpacts += groupedSteps.DrainImpacts().Sum(impact => impact.Particles);
            }

            Assert.Equal(AtlasHash(fixedSteps), AtlasHash(groupedSteps));
            Assert.Equal(fixedSteps.GetDiagnostics(), groupedSteps.GetDiagnostics());
            Assert.Equal(fixedImpacts, groupedImpacts);
        }

        var diagnostics = fixedSteps.GetDiagnostics();
        Assert.Equal(720, diagnostics.Steps);
        Assert.True(diagnostics.PoolImpactParticles > 0);
        Assert.True(diagnostics.RockContacts > 0);
        Assert.Equal(0, diagnostics.NonFiniteParticles);
    }

    [Fact]
    public void ResetAfterFlowReproducesFreshSource()
    {
        var reused = new WaterFountainFluid(9f / 16);
        for (int step = 0; step < 480; step++)
        {
            reused.Advance(WaterFountainFluid.FixedStep);
            reused.DrainImpacts();
        }
        Assert.True(reused.ParticleCount > 0);
        reused.Reset();
        var fresh = new WaterFountainFluid(9f / 16);

        // Reset deliberately retains scratch storage. Every newly emitted
        // particle must receive current pressure before its first force update.
        for (int step = 0; step < 360; step++)
        {
            reused.Advance(WaterFountainFluid.FixedStep);
            fresh.Advance(WaterFountainFluid.FixedStep);
            Assert.Equal(fresh.DrainImpacts(), reused.DrainImpacts());
        }

        Assert.Equal(AtlasHash(fresh), AtlasHash(reused));
        Assert.Equal(fresh.GetDiagnostics(), reused.GetDiagnostics());
    }

    private static string AtlasHash(WaterFountainFluid fluid) =>
        Convert.ToHexString(SHA256.HashData(fluid.BuildDensityAtlas()));
}
