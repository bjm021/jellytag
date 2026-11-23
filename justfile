set shell := ["bash", "-euo", "pipefail", "-c"]

project := "Jellytag/Jellyfin.Plugin.JellyTag"
plugin_guid := "bac81a9b-ef92-4c18-a96a-7c34e4695836"
publish_dir := "artifacts/publish"

jellyfin_image := "docker.io/jellyfin/jellyfin:12.4.20261026-211555"
dev_dir := justfile_directory() / ".dev"
dev_port := env("JELLYTAG_DEV_PORT", "8097")
dev_container := "jellytag-dev"
dev_plugin_dir := dev_dir / "config/plugins/JellyTag"

# List the recipes
default:
    @just --list

# Compile the plugin and its bundled libraries (Release) into artifacts/publish
build:
    dotnet publish -c Release {{ project }} -o {{ publish_dir }}

# Run the unit tests
test:
    dotnet test -c Release tests/Jellyfin.Plugin.JellyTag.Tests

# Run every pre-commit check on the whole repo (same as CI)
lint:
    pre-commit run --all-files --show-diff-on-failure

# Run the integration tests against a real, offline Jellyfin (podman)
integration: build
    tests/integration/run.sh "{{ jellyfin_image }}" "{{ publish_dir }}" "{{ plugin_guid }}"

# Build the release zip for VERSION (e.g. 3.0.0) into artifacts/, with meta.json from build.yaml
package VERSION:
    #!/usr/bin/env bash
    set -euo pipefail
    [[ "{{ VERSION }}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "VERSION must be X.Y.Z, got {{ VERSION }}" >&2; exit 1; }
    # Jellyfin parses System.Version, where a missing 4th component is -1 (3.0.0 < 3.0.0.0).
    version="{{ VERSION }}.0"
    meta="Jellytag/build.yaml"
    name=$(yq -r '.name' "$meta" | tr '[:upper:]' '[:lower:]')
    out="artifacts/$name-$version"
    rm -rf "$out" "$out.zip" "{{ publish_dir }}"
    mkdir -p "$out"

    dotnet publish -c Release -p:Version="{{ VERSION }}" "{{ project }}" -o "{{ publish_dir }}"
    for artifact in $(yq -r '.artifacts[]' "$meta"); do cp "{{ publish_dir }}/$artifact" "$out/"; done

    yq -o=json "{
      \"guid\": .guid, \"name\": .name, \"version\": \"$version\", \"targetAbi\": .targetAbi,
      \"framework\": .framework, \"owner\": .owner, \"category\": .category,
      \"overview\": .overview, \"description\": .description, \"changelog\": .changelog,
      \"timestamp\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"
    }" "$meta" > "$out/meta.json"

    (cd "$out" && zip -q -X "../$name-$version.zip" ./*)
    md5=$(md5sum "$out.zip" | cut -d' ' -f1)

    # Release notes from the Conventional Commits since the previous tag.
    tag="v{{ VERSION }}"
    if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
      git cliff --current --strip all > "$out.md"
    else
      git cliff --unreleased --tag "$tag" --strip all > "$out.md"
    fi
    printf '\n---\nMD5 `%s`\n' "$md5" >> "$out.md"

    echo "$out.zip  $md5"
    echo "$out.md"

# Check that tool versions pinned outside the flake match what the flake provides
check-pins:
    #!/usr/bin/env bash
    set -euo pipefail
    status=0
    check() {
      local name=$1 flake=$2 pinned=$3 where=$4
      if [[ "$flake" == "$pinned" ]]; then
        echo "ok    $name $flake"
      else
        echo "DRIFT $name: flake provides $flake, $where says $pinned"
        status=1
      fi
    }
    check "dotnet SDK" \
      "$(nix eval --inputs-from . --raw nixpkgs#dotnet-sdk_10.version)" \
      "$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json)" \
      global.json
    for workflow in .github/workflows/ci.yml .github/workflows/release.yml; do
      [[ -f "$workflow" ]] || continue
      check just \
        "$(nix eval --inputs-from . --raw nixpkgs#just.version)" \
        "$(sed -n 's/.*just-version: *//p' "$workflow")" \
        "$workflow"
    done
    if [[ -f .github/workflows/release.yml ]]; then
      check git-cliff \
        "$(nix eval --inputs-from . --raw nixpkgs#git-cliff.version)" \
        "$(sed -n 's/.*tool: *git-cliff@//p' .github/workflows/release.yml)" \
        .github/workflows/release.yml
    fi
    exit "$status"

# Start the local Jellyfin test instance
dev-up: dev-media
    #!/usr/bin/env bash
    set -euo pipefail
    mkdir -p "{{ dev_dir }}"/{config,cache,media} "{{ dev_plugin_dir }}"
    cat > "{{ dev_plugin_dir }}/meta.json" <<'EOF'
    {
      "guid": "{{ plugin_guid }}",
      "name": "JellyTag",
      "version": "0.0.0.0",
      "targetAbi": "12.0.0.0",
      "owner": "Atilili",
      "category": "General",
      "description": "Local development build."
    }
    EOF
    podman run -d --replace --name "{{ dev_container }}" \
      -p "{{ dev_port }}:8096" \
      -v "{{ dev_dir }}/config:/config:Z" \
      -v "{{ dev_dir }}/cache:/cache:Z" \
      -v "{{ dev_dir }}/media:/media:Z" \
      "{{ jellyfin_image }}" >/dev/null
    echo "Jellyfin: http://localhost:{{ dev_port }}"

# Generate test movies covering resolutions, audio layouts and languages, each with a poster
dev-media:
    tests/fixtures/media.sh "{{ dev_dir }}/media"
    find "{{ dev_dir }}/media" -name '*.mkv' | sort

# Build, install the plugin into the local instance and restart it
dev-deploy: build
    #!/usr/bin/env bash
    set -euo pipefail
    since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    # Stop before copying: Jellyfin reads the loaded DLLs from disk, overwriting them live
    # makes the old plugin crash on shutdown ("Bad IL range").
    # `podman restart` races rootless pasta for the host port: stop, then retry start.
    podman stop "{{ dev_container }}" >/dev/null
    # Drop DLLs from previous builds (e.g. libraries no longer bundled) and the rendered
    # image cache, so what is tested is exactly the new build.
    rm -f "{{ dev_plugin_dir }}"/*.dll "{{ dev_plugin_dir }}"/*.pdb
    rm -rf "{{ dev_dir }}/config/plugins/Jellyfin.Plugin.JellyTag/cache"
    cp "{{ publish_dir }}"/*.dll "{{ publish_dir }}"/*.pdb "{{ dev_plugin_dir }}/"
    for _ in $(seq 10); do podman start "{{ dev_container }}" >/dev/null 2>&1 && break; sleep 1; done
    timeout 180 bash -c 'until curl -sf http://localhost:{{ dev_port }}/health >/dev/null; do sleep 2; done'
    podman logs --since "$since" "{{ dev_container }}" 2>&1 | grep -E 'JellyTag|\[ERR\]' || true

# Follow the JellyTag lines of the Jellyfin log
dev-logs:
    podman logs -f "{{ dev_container }}" 2>&1 | grep --line-buffered -E 'JellyTag|\[ERR\]'

# Stop the local instance (data in .dev/ is kept)
dev-down:
    podman stop "{{ dev_container }}"
