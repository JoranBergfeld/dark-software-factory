# Dark Software Factory

Dark Software Factory is a blueprint for provisioning isolated product factories whose agentic loops build and operate software under operator-governed maturity settings.

## Language

**GitHub Cloud Agent**:
GitHub's managed coding agent that takes assigned issues, produces pull requests, and runs under GitHub-managed identity.
_Avoid_: GitHub Copilot Coding Agent, Cloud Agent

**Creation phase**:
The DSF phase that turns accepted work issues into reviewed code changes.
_Avoid_: Coding squad, build loop

**Operation phase**:
The DSF phase that observes production, responds to incidents, and can feed fix-forward work back to the Creation phase.
_Avoid_: SRE loop

**Maturity setting**:
An operator-controlled autonomy level, expressed as low, medium, or high per phase, that determines how much human approval remains in a factory loop.
_Avoid_: Maturity dial

**Paved road**:
A product archetype or template that carries the expected language, framework, deployment path, and default security posture for a provisioned product factory.
_Avoid_: Stack, template

**Paved-road security baseline**:
The minimum repository protections every provisioned product receives, with maturity-specific review enforcement layered on top.
_Avoid_: Security defaults

**Break-glass bypass**:
A named, time-limited, reasoned exception to a repository protection, recorded in an auditable system.
_Avoid_: Admin override

**Last healthy boundary**:
The furthest deployment cohort proven healthy before rollout pauses; autonomous recovery does not expand traffic beyond it.
_Avoid_: Safe point

**Recovery Cloud Agent session**:
A bounded GitHub Cloud Agent session started from an Operation incident to prepare a Creation-phase fix for a failed deployment.
_Avoid_: Hotfix agent
