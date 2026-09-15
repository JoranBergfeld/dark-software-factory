using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private static readonly JsonSerializerOptions BoundaryInputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static Command BuildBoundaryPlanCommand(ICliTerminal terminal, RepositoryAttachmentReviewer? reviewer, IAzureCliRunner? azure)
    {
        var input = StringOption("--input", "complete versioned application selection JSON file");
        var json = BoolOption("--json", "print the read-only categorized application boundary plan");
        var fingerprint = StringOption("--reviewed-fingerprint", "reject replay unless the fresh categorized plan matches this fingerprint");
        var command = new Command("plan", "preview a dedicated application boundary; never reserve, provision or activate");
        AddOptions(command, input, json, fingerprint);
        command.SetAction(async (parse, cancellationToken) =>
        {
            try
            {
                var path = parse.GetValue(input);
                var interactive = string.IsNullOrWhiteSpace(path) && terminal.Capabilities.IsInteractive && !parse.GetValue(json);
                if (string.IsNullOrWhiteSpace(path) && !interactive)
                    throw new BoundaryInputException("--input is required with complete explicit planning inputs when prompts are unavailable.");

                var reader = new AzureBoundaryReader(azure ?? new SystemAzureCliRunner());
                var planner = new ApplicationBoundaryPlanner(reviewer, reader);
                var interaction = new BoundaryPlanInteraction(terminal, reader);
                var selection = interactive ? await interaction.SelectAsync(cancellationToken)
                    : JsonSerializer.Deserialize<ApplicationBoundarySelection>(
                        await File.ReadAllTextAsync(path!, cancellationToken), BoundaryInputJson) ?? throw new JsonException();
                if (interactive)
                {
                    var draft = await planner.PlanAsync(selection, cancellationToken);
                    BoundaryPlanPresentation.Write(terminal, draft, ReviewJson);
                    if (draft.State is not (BoundaryPlanState.Blocked or BoundaryPlanState.Unknown or BoundaryPlanState.Failed))
                        selection = interaction.Review(draft);
                }

                var plan = await planner.PlanAsync(selection, cancellationToken, parse.GetValue(fingerprint));
                if (!parse.GetValue(json))
                {
                    BoundaryPlanPresentation.Write(terminal, plan, ReviewJson);
                    terminal.WriteLine("[dsf] Explicit replay: save the final JSON selection field as selection.json, then run:");
                    terminal.WriteLine("dsf onboard decide plan --input selection.json --reviewed-fingerprint " + plan.Fingerprint);
                    terminal.WriteLine("[dsf] Final plan JSON:");
                }
                terminal.WriteLine(JsonSerializer.Serialize(plan, ReviewJson));
                return plan.State == BoundaryPlanState.Reviewed ? Success : Failure;
            }
            catch (BoundaryPlanningCanceledException)
            {
                Outcome(BoundaryPlanState.Canceled, "Operator canceled selection; no remote changes.");
                return CanceledExitCode;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                Outcome(BoundaryPlanState.Failed, "--input must be a readable, complete version-1 selection JSON file; null, unknown or invalid fields are rejected.");
                return Failure;
            }
            catch (BoundaryInputException exception)
            {
                Outcome(BoundaryPlanState.Failed, exception.Message);
                return Failure;
            }
            catch (BoundaryInspectionException exception)
            {
                Outcome(BoundaryPlanState.Unknown, exception.Message);
                return Failure;
            }

            void Outcome(BoundaryPlanState state, string detail) => terminal.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1, state, detail, hasRemoteEffects = false,
                nextActor = "operator / administrator",
                nextAction = "Correct complete explicit inputs or establish read access, then run read-only planning again. No forced retry or greenfield fallback.",
            }, ReviewJson));
        });
        return command;
    }
}
