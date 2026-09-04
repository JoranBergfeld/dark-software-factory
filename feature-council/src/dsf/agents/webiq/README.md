# WebIQ source agent — Python parity reference (frozen)

This package is the pre-cutover Python implementation of the WebIQ source
agent. It is kept only as a **parity reference** for the #149 cutover
acceptance and is not operator or contributor documentation: do not build,
run, or test the product with Python, `uv`, `pytest`, or this package's
fixtures.

For the shipped product, see `docs/site/get-started/operate.md`. The .NET
parity implementation of this source agent (WebIQ/Tavily web-search
evidence) lands under the #149 cutover work; until then this package's
behavior is the acceptance baseline it must match.
