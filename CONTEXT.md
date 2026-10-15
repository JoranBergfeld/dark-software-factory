# Dark Software Factory

Dark Software Factory is a blueprint for provisioning isolated product factories whose agentic loops build and operate software under operator-governed maturity settings.

## Language

**Product factory**:
An isolated DSF instance serving one product under an owner's governance, distinct from the application's deployment.
_Avoid_: Application environment

**Factory ID**:
The unique, immutable identity of a product factory, independent of its product key, repository name, or deployed infrastructure.
_Avoid_: Product key, resource ID

**Product key**:
The owner-scoped name identifying a product in that owner's factory registry.
_Avoid_: Factory ID, repository name

**Application environment**:
A particular deployed environment of a product, such as production, distinct from the environment hosting its factory.
_Avoid_: Factory environment

**Application boundary**:
The explicitly selected application resources belonging to one product's application environment.
_Avoid_: Monitored resource group

**Evidence backend**:
A service storing evidence about an application environment, distinct from the application resources that evidence describes.
_Avoid_: Application boundary

**Observation claim**:
An owner-governed declaration attaching a resource to a Decide factory, without transferring ownership of the application's delivery or operation or establishing global exclusivity.
_Avoid_: Application ownership, resource tag

**Factory manifest**:
The durable description of a factory's identity, application boundary, and desired configuration.
_Avoid_: Execution plan

**Product charter**:
The human-owned statement of a product's purpose, intended users, goals, and boundaries that guides its factory's decisions.
_Avoid_: Code summary, implementation plan

**Council readiness**:
The assessed state in which a factory's intent, evidence, models, and access satisfy the conditions for proposal publication.
_Avoid_: Provisioning completion, activation approval

**Activation approval**:
An operator's permission for a product factory to publish qualifying proposals under a reviewed configuration, distinct from whether the factory is currently ready.
_Avoid_: Charter approval, council readiness

**Council offboarding**:
The deliberate detachment of a product factory from its application environment, releasing its observation claims and factory-specific access while retaining disabled factory infrastructure and audit records.
_Avoid_: Council pause, application teardown

**Council reattachment**:
The explicitly reviewed return of an offboarded factory to the same product and application environment under its retained identity, requiring renewed access, readiness, and activation approval.
_Avoid_: Automatic recovery, new onboarding

**Factory purge**:
The separately approved permanent removal of retained factory infrastructure after council offboarding, retiring its factory identity and preserving required audit evidence through an approved disposition.
_Avoid_: Council offboarding, application deletion

**Factory proposal**:
A proposal attributable to a particular product factory, offered for human consideration rather than accepted for delivery.
_Avoid_: Work assignment, delivery handoff

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
