"""The council->squad handoff contract: one well-known label.

Every issue the feature council files carries :data:`HANDOFF_LABEL`, and the
coding squad's ``squad triage`` keys on exactly that label. Keeping it a single
system-level constant (rather than per-product taxonomy data) means the contract
cannot drift per product, and the routing station stays independent of each
product's label-taxonomy contents.
"""

from __future__ import annotations

HANDOFF_LABEL = "squad:ready"
HANDOFF_LABEL_DESCRIPTION = "Council-filed issue ready for coding-squad triage"
HANDOFF_LABEL_COLOR = "1d76db"

__all__ = ["HANDOFF_LABEL", "HANDOFF_LABEL_DESCRIPTION", "HANDOFF_LABEL_COLOR"]
