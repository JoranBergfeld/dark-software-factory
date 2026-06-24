"""Well-known label contracts: the council->creation handoff and the SRE->council
incident marker.

Every issue the feature council files carries :data:`HANDOFF_LABEL`. Council S7
files the routed issue under this label and assigns it to the GitHub Copilot
Coding Agent (ADR 0016); the label is the durable, system-level marker of a
council->creation handoff. Keeping it a single system-level constant (rather than
per-product taxonomy data) means the contract cannot drift per product, and the
routing station stays independent of each product's label-taxonomy contents.
"""

from __future__ import annotations

HANDOFF_LABEL = "creation:ready"
HANDOFF_LABEL_DESCRIPTION = "Council-filed issue ready for the creation phase (Coding Agent)"
HANDOFF_LABEL_COLOR = "1d76db"

#: The SRE->council marker. The managed Azure SRE Agent stamps this on every
#: incident issue it files; the council's ``incidents`` source pulls only issues
#: carrying it. Because council-filed issues carry :data:`HANDOFF_LABEL` and never
#: this label, the incidents source cannot re-ingest council output (no loop).
INCIDENT_LABEL = "incident"
INCIDENT_LABEL_DESCRIPTION = "SRE-filed incident the feature council reflects on"
INCIDENT_LABEL_COLOR = "b60205"

__all__ = [
    "HANDOFF_LABEL",
    "HANDOFF_LABEL_DESCRIPTION",
    "HANDOFF_LABEL_COLOR",
    "INCIDENT_LABEL",
    "INCIDENT_LABEL_DESCRIPTION",
    "INCIDENT_LABEL_COLOR",
]
