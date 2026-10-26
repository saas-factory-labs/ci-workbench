#!/usr/bin/env bash
# Packs the three CI Workbench libraries, inspects the packages, then restores and runs a minimal external host that
# references ONLY the Components package from a clean package cache. Exits non-zero on the first failed check.
# Usage: scripts/verify-packages.sh [port]   (run from anywhere; needs the .NET SDK, unzip and curl)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
port="${1:-5391}"
out="$root/artifacts/nupkgs"
version="$(sed -n 's#.*<CIWorkbenchVersion>\(.*\)</CIWorkbenchVersion>.*#\1#p' "$root/Directory.Build.props")"
ids=(Core GitHub Components)

fail() { echo "FAIL: $*" >&2; exit 1; }

rm -rf "$out"
for n in "${ids[@]}"; do
  dotnet pack "$root/src/CIWorkbench.$n/CIWorkbench.$n.csproj" -c Release -o "$out" --nologo -v q
done

for n in "${ids[@]}"; do
  pkg="$out/SaaS-Factory.CIWorkbench.$n.$version.nupkg"
  [ -f "$pkg" ] || fail "missing $pkg"
  listing="$(unzip -l "$pkg")"
  grep -q ' LICENSE$' <<<"$listing" || fail "$n: LICENSE not packed"
  grep -q ' README.md$' <<<"$listing" || fail "$n: README.md not packed"
  nuspec="$(unzip -p "$pkg" '*.nuspec')"
  grep -q "<id>SaaS-Factory.CIWorkbench.$n</id>" <<<"$nuspec" || fail "$n: unexpected package id"
  grep -q '<license type="file">LICENSE</license>' <<<"$nuspec" || fail "$n: licence is not the LICENSE file"
  grep -qi 'CIWorkbench.Web\|AppHost\|ServiceDefaults' <<<"$nuspec" && fail "$n: depends on a standalone-host project"
done

comp="$(unzip -l "$out/SaaS-Factory.CIWorkbench.Components.$version.nupkg")"
for asset in ciworkbench.css ciworkbench.js ciw-theme.js fonts/Inter-Variable.woff2; do
  grep -q "staticwebassets/$asset" <<<"$comp" || fail "Components: static asset $asset not packed"
done
comp_spec="$(unzip -p "$out/SaaS-Factory.CIWorkbench.Components.$version.nupkg" '*.nuspec')"
for dep in Core GitHub; do
  grep -q "id=\"SaaS-Factory.CIWorkbench.$dep\"" <<<"$comp_spec" || fail "Components does not depend on $dep"
done
unzip -p "$out/SaaS-Factory.CIWorkbench.GitHub.$version.nupkg" '*.nuspec' | grep -q 'id="SaaS-Factory.CIWorkbench.Core"' || fail "GitHub does not depend on Core"
echo "package contents OK"

# Clean consumer: fresh package cache, sources limited to the packed feed and nuget.org, no repository props.
cache="$(mktemp -d)"
consumer="$root/tests/PackageConsumer"
rm -rf "$consumer/bin" "$consumer/obj"
export NUGET_PACKAGES="$cache"
dotnet build "$consumer/PackageConsumer.csproj" -c Release --nologo -v q -p:CIWorkbenchPackageVersion="$version"
[ -d "$cache/saas-factory.ciworkbench.core/$version" ] || fail "Core did not arrive transitively"
[ -d "$cache/saas-factory.ciworkbench.github/$version" ] || fail "GitHub did not arrive transitively"

log="$(mktemp)"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$port" \
  dotnet "$consumer/bin/Release/net10.0/PackageConsumer.dll" >"$log" 2>&1 &
pid=$!
trap 'kill $pid 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do curl -fs -o /dev/null "http://127.0.0.1:$port/" && break; sleep 0.5; done

page="$(curl -fs "http://127.0.0.1:$port/")" || { cat "$log"; fail "consumer page did not load"; }
grep -q 'data-testid="source-pill"' <<<"$page" || fail "packaged OverviewDashboard did not render"
for asset in ciworkbench.css ciworkbench.js ciw-theme.js; do
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/_content/CIWorkbench.Components/$asset")"
  [ "$code" = "200" ] || fail "static asset $asset returned $code"
done
echo "clean consumer OK (host rendered the packaged component and served the packaged assets)"
