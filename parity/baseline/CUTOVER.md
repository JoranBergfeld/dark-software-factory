# Final cutover checklist

This baseline is acceptance evidence, not an executable implementation or rollback route.

Before merging the cutover commit:

1. Confirm the .NET CI, parity matrix, protected live-smoke evidence, release artifacts, and
   six-RID smoke gates have passed for the merge candidate.
2. Verify the local annotated `archive/python-final` tag identifies the final pre-cutover
   commit, then push that tag with the merge.

After the cutover commit is merged:

1. Confirm the protected release workflow and live-smoke evidence are attached to the merged
   commit.
2. Delete the migration branch after merge.
3. Handle any post-cutover failure by hotfix .NET; do not restore the removed implementation.
