# Verify installed development clients

## Task

The user installed Claude Code and Gemini CLI and asked for the next step. Check the installed clients and activate only routes supported by observed authentication and model access.

## Workflow

- Inspect the shared PStack entrypoint, compatibility rules, model policy, project context, and previous model-policy handoff.
- Throughput checkpoint: n/a, client verification and a bounded configuration update. No application code or architecture changes.
- Use installed CLI help and official documentation to check authentication and explicit effort controls. No architectural explanation or parallel implementation is needed.
- Apply Prove It Works. Distinguish installation, login, successful model access, and completed review. Apply Unslop to the handoff and user instructions.
- Activate the verified Opus route and check the setup. Leave Gemini pending its isolated-profile login and a model check.
- No commit, PR, or application implementation. The previously required Opus review remains outstanding.

## Observed results

- Sandboxed command discovery did not find the newly installed clients. Read-only discovery outside the sandbox, with the current user and machine PATH available to that process, found both clients. No persistent PATH settings were changed.
- Claude Code reports version `2.1.288`. Its `auth status --json` command reports an authenticated first-party subscription session. Account identifiers and credentials were not copied into repository records.
- Gemini CLI reports version `0.62.0`. Its project profile at `.pstack/runtime/gemini-home` does not exist yet. Installing or logging into the ordinary Gemini profile does not initialize this separate profile.
- A Claude request selected exact model `claude-opus-5-5` and explicit `xhigh`, using both the native effort flag and a process-local effort environment variable. Tools and MCP access were disabled, and session persistence was disabled. The request returned the expected `IDEVELOP_MODEL_OK` response. CLI result metadata identified only `claude-opus-5-5` for model usage and reported no error.
- `node scripts/model-policy.mjs resolve backend-review` now reports ready with Opus at xhigh.
- `node scripts/pstack.mjs check` passed for 49 generated skills, clean pinned upstream, shared links, and model configuration.

## Changed artifacts

- `.pstack/models.json` activates the exact Opus model with `client-catalog` verification.
- `.pstack/compatibility.md`, `README.md`, and `docs/context.md` distinguish verified Opus access from pending Gemini activation.
- This handoff records the evidence and remaining manual step. No launcher, validator, or application code changed.

## Next action

In a new PowerShell window, enter the repository, set `GEMINI_CLI_HOME` to `.pstack/runtime/gemini-home`, and start `gemini --model gemini-3.8-flash`. Complete the browser sign-in using the Google account associated with the intended subscription. Accept this project's trust prompt if shown, then exit without starting implementation.

The [Gemini authentication guide](https://geminicli.com/docs/get-started/authentication/) documents Google sign-in for subscription access. The [configuration reference](https://geminicli.com/docs/reference/configuration/) documents `GEMINI_CLI_HOME` and its nested `.gemini` profile directory. [Claude model configuration](https://code.claude.com/docs/en/model-config) documents explicit model and effort controls.

After sign-in, verify Gemini's exact model access, explicit high thinking setting, and project skill isolation before activating its route. Then run the required cross-provider setup review. Model availability does not imply that review has occurred. The model-policy resolver prepares role assignments; it does not automatically launch or coordinate external clients.
