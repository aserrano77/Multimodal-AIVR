using Autonomy.Domain;
using NUnit.Framework;

namespace Autonomy.BT.Tests
{
    [TestFixture]
    public class ExperimentRuntimeMetadataResolverTests
    {
        [Test]
        public void Resolve_Uses_Active_Runtime_Metadata_When_Manual_Is_Empty()
        {
            ExperimentRuntimeMetadataResolution resolution = ExperimentRuntimeMetadataResolver.Resolve(
                string.Empty,
                string.Empty,
                "Arcade",
                "FastDemo");

            Assert.That(resolution.EffectiveDriveProfile, Is.EqualTo("Arcade"));
            Assert.That(resolution.EffectiveAutonomyPolicy, Is.EqualTo("FastDemo"));
            Assert.That(resolution.HasMismatch, Is.False);
            Assert.That(resolution.ResolutionStrategy, Is.EqualTo("active_runtime_metadata_filled_empty_fields"));
        }

        [Test]
        public void Resolve_Uses_Active_Runtime_Metadata_And_Flags_Mismatch()
        {
            ExperimentRuntimeMetadataResolution resolution = ExperimentRuntimeMetadataResolver.Resolve(
                "Realistic",
                "Safe",
                "Arcade",
                "FastDemo");

            Assert.That(resolution.EffectiveDriveProfile, Is.EqualTo("Arcade"));
            Assert.That(resolution.EffectiveAutonomyPolicy, Is.EqualTo("FastDemo"));
            Assert.That(resolution.HasMismatch, Is.True);
            Assert.That(resolution.ResolutionStrategy, Is.EqualTo("active_runtime_metadata_overrode_manual"));
        }

        [Test]
        public void Resolve_Keeps_Manual_Metadata_When_Runtime_Unavailable()
        {
            ExperimentRuntimeMetadataResolution resolution = ExperimentRuntimeMetadataResolver.Resolve(
                "Realistic",
                "Safe",
                string.Empty,
                string.Empty);

            Assert.That(resolution.EffectiveDriveProfile, Is.EqualTo("Realistic"));
            Assert.That(resolution.EffectiveAutonomyPolicy, Is.EqualTo("Safe"));
            Assert.That(resolution.HasActiveRuntimeMetadata, Is.False);
            Assert.That(resolution.HasMismatch, Is.False);
            Assert.That(resolution.ResolutionStrategy, Is.EqualTo("manual_metadata_used_runtime_unavailable"));
        }

        [Test]
        public void Resolve_Does_Not_Flag_Matching_Manual_Metadata()
        {
            ExperimentRuntimeMetadataResolution resolution = ExperimentRuntimeMetadataResolver.Resolve(
                "Arcade",
                "FastDemo",
                "Arcade",
                "FastDemo");

            Assert.That(resolution.EffectiveDriveProfile, Is.EqualTo("Arcade"));
            Assert.That(resolution.EffectiveAutonomyPolicy, Is.EqualTo("FastDemo"));
            Assert.That(resolution.HasMismatch, Is.False);
            Assert.That(resolution.ResolutionStrategy, Is.EqualTo("manual_metadata_matches_runtime"));
        }
    }
}
