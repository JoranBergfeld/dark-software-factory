# Control Center Parity Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a throwaway browser prototype that validates the product-scoped Control Center
operator experience selected in the Wayfinder decision.

**Architecture:** A single self-contained HTML document lives beside the Control Center module
and maintains simulated effective policy in memory. It provides a product selector, allowlisted
numeric edits, scoped switches, a confirmation-gated global dry-run control, disabled
unsupported writes, and an always-visible JSON state view.

**Tech Stack:** Browser HTML, CSS, and vanilla JavaScript; no runtime dependencies or
persistence.

---

### Task 1: Create the standalone operator dashboard prototype

**Files:**
- Create: `control-center/src/dsf/control_center/control-center-parity.prototype.html`
- Test: Manual browser interaction only; this is explicitly throwaway prototype code.

- [ ] **Step 1: Create the prototype document**

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>PROTOTYPE: Control Center parity</title>
  <style>
    :root { color-scheme: dark; font-family: system-ui, sans-serif; }
    body { background: #10141b; color: #e6edf3; margin: 0; }
    header, main { max-width: 1100px; margin: auto; padding: 20px; }
    .notice { background: #26344a; border-left: 4px solid #6cb6ff; padding: 12px; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 16px; }
    section { background: #1a202b; border: 1px solid #343d4d; border-radius: 8px; padding: 16px; }
    .danger { border-color: #f47067; } .active { color: #f47067; font-weight: 700; }
    .row { display: flex; justify-content: space-between; align-items: center; gap: 12px; margin: 10px 0; }
    button, select, input { font: inherit; padding: 6px 9px; }
    button[disabled] { opacity: .5; } pre { overflow: auto; background: #0d1117; padding: 12px; }
    .muted { color: #a8b3c7; } label { display: block; margin-top: 10px; }
  </style>
</head>
<body>
  <header>
    <p class="notice"><strong>THROWAWAY PROTOTYPE.</strong> Browser session + CSRF is active;
      bearer authentication remains available for automated API clients.</p>
    <h1>Control Center</h1>
    <label>Selected product
      <select id="product"><option>alpha</option><option>beta</option></select>
    </label>
  </header>
  <main class="grid">
    <section>
      <h2>Effective product policy</h2>
      <p class="muted">Changes apply to this product’s next run.</p>
      <div id="switches"></div>
      <label>Confidence threshold <input id="threshold" type="number" min="0" max="1" step=".01"></label>
      <label>Grounding weight <input id="weight" type="number" min="0" step=".05"></label>
      <button id="save">Save validated values</button>
      <p id="validation" class="muted"></p>
    </section>
    <section class="danger">
      <h2>Global dry-run</h2>
      <p id="dry-run-state"></p>
      <button id="dry-run">Change global dry-run</button>
      <p class="muted">This affects every product and requires confirmation.</p>
    </section>
    <section>
      <h2>Unavailable controls</h2>
      <button disabled>Force a run now</button>
      <p class="muted">Unsupported in the Control Center. Use <code>dsf sweep</code> from an
        authenticated operator shell.</p>
    </section>
    <section>
      <h2>Effective state</h2>
      <pre id="state"></pre>
    </section>
  </main>
  <script>
    const policies = {
      alpha: { critics: true, agents: true, scheduledTrigger: true, threshold: .72, weight: 1 },
      beta: { critics: true, agents: false, scheduledTrigger: true, threshold: .8, weight: 1.1 }
    };
    let dryRun = false;
    const product = document.querySelector("#product");
    const fields = [
      ["critics", "Council critics"],
      ["agents", "Source agents"],
      ["scheduledTrigger", "Scheduled trigger"]
    ];

    function selected() { return policies[product.value]; }
    function render() {
      const policy = selected();
      document.querySelector("#switches").innerHTML = fields.map(([key, label]) =>
        `<div class="row"><span>${label}</span><button data-key="${key}">${policy[key] ? "Enabled" : "Disabled"}</button></div>`
      ).join("");
      document.querySelector("#threshold").value = policy.threshold;
      document.querySelector("#weight").value = policy.weight;
      document.querySelector("#dry-run-state").textContent = dryRun ? "ACTIVE: filing is disabled." : "Inactive: filing is enabled.";
      document.querySelector("#dry-run-state").className = dryRun ? "active" : "";
      document.querySelector("#state").textContent = JSON.stringify({
        authenticatedBy: "session-cookie + CSRF",
        product: product.value,
        effectivePolicy: policy,
        globalDryRun: dryRun
      }, null, 2);
    }
    document.querySelector("#switches").addEventListener("click", event => {
      const key = event.target.dataset.key;
      if (!key) return;
      selected()[key] = !selected()[key];
      render();
    });
    product.addEventListener("change", render);
    document.querySelector("#save").addEventListener("click", () => {
      const threshold = Number(document.querySelector("#threshold").value);
      const weight = Number(document.querySelector("#weight").value);
      const validation = document.querySelector("#validation");
      if (!Number.isFinite(threshold) || threshold < 0 || threshold > 1 || !Number.isFinite(weight) || weight < 0) {
        validation.textContent = "Use a threshold from 0 to 1 and a non-negative weight.";
        return;
      }
      Object.assign(selected(), { threshold, weight });
      validation.textContent = "Saved. The effective state below has been updated.";
      render();
    });
    document.querySelector("#dry-run").addEventListener("click", () => {
      if (confirm(`Change global dry-run to ${dryRun ? "inactive" : "active"}?`)) {
        dryRun = !dryRun;
        render();
      }
    });
    render();
  </script>
</body>
</html>
```

- [ ] **Step 2: Open the prototype**

Run: `xdg-open control-center/src/dsf/control_center/control-center-parity.prototype.html`

Expected: A dark product-scoped dashboard opens; selecting `alpha` or `beta`, toggling an
allowlisted switch, saving valid numeric values, and changing dry-run visibly refresh the
effective-state JSON.

- [ ] **Step 3: Commit the throwaway asset**

```bash
git add control-center/src/dsf/control_center/control-center-parity.prototype.html
git commit -m "chore: add control center parity prototype" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Capture the Wayfinder decision

**Files:**
- Modify: GitHub issue **Prototype the operable Control Center parity target** (#119)
- Modify: GitHub issue **Chart the repository-wide migration from Python to .NET** (#107)

- [ ] **Step 1: Review the prototype with the user**

Verify that browser authentication is displayed as a session-cookie plus CSRF flow; bearer
authentication is retained only for API automation; controls are product-first; numeric fields
are allowlisted and validated; global dry-run requires confirmation; and unsupported writes are
explained rather than ignored.

- [ ] **Step 2: Record the decision and close the ticket**

```bash
gh issue comment 119 --body "## Resolution

The .NET Control Center will use session-cookie authentication plus CSRF protection for browser
writes, while bearer authentication remains an API-only automation path. Its primary dashboard is
product-first, showing effective policy controls. Numeric writes are allowlisted and range
validated. Dry-run is a globally scoped, prominent emergency switch requiring confirmation.
Unsupported writes are visible but disabled with a reason and supported alternative.

Prototype asset: branch \`research/control-center-parity\`."
gh issue close 119
```

- [ ] **Step 3: Append the map context pointer**

Add this line under `## Decisions so far` in **Chart the repository-wide migration from Python to
.NET** (#107):

```markdown
- [Prototype the operable Control Center parity target](https://github.com/JoranBergfeld/dark-software-factory/issues/119): Use product-first controls, cookie-plus-CSRF browser writes, validated numeric policy fields, a confirmed global dry-run switch, and explained disabled unsupported writes.
```

