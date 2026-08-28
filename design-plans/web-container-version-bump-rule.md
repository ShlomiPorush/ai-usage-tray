# Require a new container version for Web View changes

Written against: 2d5f1ae15c0699a06c2831f721c7ac89b01afd14

## Evidence chain

- Surface: `AGENTS.md`, `web/`, `remote/server/Dockerfile`, `remote/server/VERSION`, `remote/server/README.md`, and `.github/workflows/container.yml`
- Problem: `remote/server/Dockerfile` copies all of `web/` into the published image, while the Container workflow reads the tag from `remote/server/VERSION`. Main currently contains Web View changes while the container version and documented pinned tag remain `1.0.4`, so the workflow republishes changed image content under an existing semantic version.
- Design evidence: `remote/server/README.md` states that `VERSION` owns the container tag and OCI version label. The user explicitly requires every Web View change to receive a new container version rather than republishing the same version.
- Owner: `remote/server/VERSION` owns the image version. `AGENTS.md` owns contributor rules. `remote/server/README.md` owns the user-facing pinned image example.
- Scope and affected surfaces: Contributor instructions, current container version, documented image tag, and future changes under `web/` that are copied into the server image.
- Uncertainty: None. The current corrective bump is patch-level because it versions already-merged compatible Web View changes without changing the container API contract.

## Design decision

Add a hard repository rule that every change under `web/` must increment `remote/server/VERSION` in the same change and update the pinned version example in `remote/server/README.md`. State explicitly that changed container content must never be published under an existing version tag. Apply the rule immediately by moving the current container version from `1.0.4` to `1.0.5` and aligning the README example.

Keep the existing `latest` and commit-SHA tags. The semantic version remains the immutable human-facing image version, while `latest` continues to identify the newest image from `main`.

## Reuse

- `remote/server/VERSION` as the existing single source of truth for container versions.
- The pinned `ghcr.io/shlomiporush/ai-usage-tray:<version>` example in `remote/server/README.md`.
- The existing Container workflow's raw version, `latest`, and `sha-` tag generation.
- Exemplar: The existing `AGENTS.md` rule that couples `web/` changes to Worker bundle regeneration and service-worker cache bumps.

No new version file or workflow input is required.

## Changes

1. `AGENTS.md`
   - Change: Extend the `web/` architecture rule with: any change under `web/` must bump `remote/server/VERSION` in the same change because the container embeds that directory; update the pinned version in `remote/server/README.md`; never reuse a semantic container version for changed image content.
   - Preserve: Worker bundle regeneration, service-worker cache bump requirements, release ownership, and all existing container verification commands.
   - Verify: An agent reading only `AGENTS.md` can identify both generated artifacts and both version files that a Web View change requires.

2. `remote/server/VERSION`
   - Change: Increment `1.0.4` to `1.0.5` for the compatible Web View and Web Push changes already present on `main`.
   - Preserve: Plain `major.minor.patch` format and use as the Docker build argument, raw image tag, embedded file, and OCI label.
   - Verify: CI reads `1.0.5`, the built image label is `1.0.5`, and `/app/server/VERSION` contains `1.0.5`.

3. `remote/server/README.md`
   - Change: Replace the pinned `:1.0.4` image example with `:1.0.5`.
   - Preserve: The `latest` example, deployment instructions, and statement that `VERSION` owns the image version.
   - Verify: Documentation and `remote/server/VERSION` show the same tag.

## Scope

- Inherit: Future changes under `web/` receive the new contributor requirement because that directory is copied into both the Node container and Worker bundle.
- Verify: Container workflow, CI container build, version label, embedded version file, README pinned tag, Worker bundle generation, and service-worker cache behavior.
- Exclude: Automatic semantic-version calculation, changing Container workflow triggers, desktop `VersionPrefix`, GitHub Release tags, production deployment, and retroactively rewriting the already-published `1.0.4` image.

## Validation

- Product: Confirm a container built after the bump serves the current Web View assets and reports healthy.
- Interface: Confirm the generated Worker bundle still matches `web/`; this rule does not change Web View presentation.
- System: Confirm `remote/server/VERSION` and the README pinned tag both equal `1.0.5`, and changed content is published only under the new semantic tag plus `latest` and `sha-<commit>`.
- Repository: `node remote/server/server.test.mjs` -> all tests pass.
- Repository: `node remote/worker/bundle.mjs` -> generated bundle is current.
- Repository: Build the container with `--build-arg VERSION=1.0.5`, then verify the OCI version label and embedded `server/VERSION` both equal `1.0.5`.

## Stop conditions

- Stop if `web/` is no longer copied into `remote/server/Dockerfile`; reassess whether a container bump is still required.
- Stop if `1.0.5` already exists with different image content in GHCR; select the next unused patch version rather than overwriting it.
- Stop before pushing, publishing a container, or changing production deployment without explicit authorization.

## Design documentation

- After acceptance and validation: the accepted versioning rule belongs in `AGENTS.md`; keep `remote/server/README.md` limited to the current version and operator-facing behavior.
