using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

public sealed class JudgmentConfigurationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null,null,null]")]
    [InlineData("[{\"provider\":\"openai\"}]")]
    [InlineData("not json")]
    public void Malformed_or_incomplete_json_rosters_fail_by_setting_name(string json)
    {
        var exception = Assert.Throws<RuntimeConfigurationException>(() =>
            JurySettings.Read(new Dictionary<string, string?> { [JurySettings.ModelsKey] = json }));

        Assert.Contains(JurySettings.ModelsKey, exception.Message);
    }

    [Fact]
    public void Typed_provisioning_models_serialize_to_the_same_runtime_contract()
    {
        var provisioned = JurySettings.Create(
        [
            new JurorModelSettings("", "openai", "gpt", "https://models.example/", "gpt-4.1"),
            new JurorModelSettings("", "deepseek", "deepseek", "https://models.example", "DeepSeek-V3"),
            new JurorModelSettings("", "xai", "grok", "https://models.example", "grok-3"),
        ]);

        var runtime = JurySettings.Read(new Dictionary<string, string?>
        {
            [JurySettings.ModelsKey] = provisioned.SerializeModels(),
        });

        Assert.Equal(provisioned.Jurors, runtime.Jurors);
        Assert.Equal(TimeSpan.FromSeconds(120), runtime.Timeout);
    }

    [Fact]
    public void Provisioning_can_validate_typed_lens_overrides_using_the_runtime_contract()
    {
        var settings = DeliberationSettings.Create(1,
            [new DeliberationLensSettings("value", true, 3), new DeliberationLensSettings("cost", false, 1)]);

        Assert.Equal(1, settings.Rounds);
        Assert.Equal(5, settings.Lenses.Count);
        Assert.Equal(3, settings.Lenses.Single(lens => lens.Name == "value").Weight);
        Assert.False(settings.Lenses.Single(lens => lens.Name == "cost").Enabled);
        Assert.True(settings.Lenses.Single(lens => lens.Name == "security").Enabled);
    }

    [Fact]
    public void A_single_json_roster_configures_three_models_with_optional_stable_names()
    {
        var settings = JurySettings.Read(new Dictionary<string, string?>
        {
            ["DSF_JURY_MODELS"] = """
                [
                  {"provider":"openai","family":"gpt","endpoint":"https://models.example","deployment":"gpt-4.1"},
                  {"name":"independent","provider":"deepseek","family":"deepseek","endpoint":"https://models.example","deployment":"DeepSeek-V3"},
                  {"provider":"xai","family":"grok","endpoint":"https://models.example","deployment":"grok-3"}
                ]
                """,
            ["DSF_JURY_TIMEOUT_SECONDS"] = "45",
        });

        Assert.Equal(["juror-1", "independent", "juror-3"], settings.Jurors.Select(juror => juror.Name));
        Assert.Equal(["gpt", "deepseek", "grok"], settings.Jurors.Select(juror => juror.Family));
        Assert.Equal(TimeSpan.FromSeconds(45), settings.Timeout);
    }
}
