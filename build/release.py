"""Small release driver. Registry writes occur only in the explicit publish command."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import xml.etree.ElementTree as ET

REPOSITORY = "dombrovsky/onvif-webhook-bridge"
SOURCE = f"https://github.com/{REPOSITORY}"
ARCHES = ("amd64", "arm64")
INDEX_TYPE = "application/vnd.oci.image.index.v1+json"
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?", re.ASCII)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def version(value):
    match = SEMVER.fullmatch(value)
    require(match is not None and len(value) <= 128,
            "Use SemVer without v or +build metadata, for example 0.1.0-rc.1 or 0.1.0 (maximum 128 characters).")
    require(all(int(n) <= 65534 for n in match.groups()[:3]), "Version components must fit .NET assembly metadata (0..65534).")
    suffix = match[4]
    require(not suffix or all(not p.isdigit() or p == "0" or not p.startswith("0") for p in suffix.split(".")),
            "Numeric prerelease identifiers cannot have leading zeroes.")
    return bool(suffix)


def source_version(xml=None):
    root = ET.fromstring(xml if xml is not None else Path("Directory.Build.props").read_text(encoding="utf-8-sig"))
    values = []
    for name in ("VersionPrefix", "VersionSuffix"):
        nodes = root.findall(f".//{name}")
        require(len(nodes) == 1 and not nodes[0].attrib, f"Define exactly one unconditional {name} in Directory.Build.props.")
        values.append(nodes[0].text or "")
    result = values[0] + ("-" + values[1] if values[1] else "")
    version(result)
    return result


def run(*args, data=None):
    result = subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        # Commands never include credentials; authentication is supplied by the Actions login steps.
        raise RuntimeError(f"{args[0]} {args[1]} failed: {result.stderr.decode(errors='replace')}")
    return result.stdout


def api(path, payload=None, method=None):
    args = ["gh", "api", f"repos/{REPOSITORY}/{path}"]
    if method:
        args += ["--method", method]
    if payload is not None:
        args += ["--input", "-"]
    return json.loads(run(*args, data=json.dumps(payload).encode() if payload is not None else None))


def digest(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def image_names(namespace):
    require(re.fullmatch(r"[a-z0-9][a-z0-9_-]{1,254}", namespace) is not None,
            "Set the DOCKERHUB_NAMESPACE repository variable to the existing Docker Hub user/organization namespace.")
    return (f"ghcr.io/{REPOSITORY}", f"docker.io/{namespace}/onvif-webhook-bridge")


def absent_image(reference):
    try:
        run("skopeo", "inspect", "--raw", f"docker://{reference}")
    except RuntimeError as error:
        # Only an explicit registry 'not found' response means absence. Auth/network failures fail closed.
        require(any(s in str(error).lower() for s in ("manifest unknown", "manifest_unknown", "name unknown", "name_unknown")),
                f"Could not establish absence of {reference}; check registry access: {error}")
        return
    raise ValueError(f"Immutable image {reference} already exists. Choose a new version; never overwrite it.")


def validate_source(selected, revision):
    prerelease = version(selected)
    require(re.fullmatch(r"[0-9a-f]{40}", revision) is not None, "Select the full 40-character commit SHA from protected main.")
    require(os.environ.get("GITHUB_REF") == "refs/heads/main", "Run Release from main in the Actions branch selector.")
    require(os.environ.get("GITHUB_REPOSITORY") == REPOSITORY, "Release publication is restricted to the upstream repository.")
    require(api("branches/main")["protected"], "Protect main with a branch rule/ruleset before releasing.")
    try:
        run("git", "merge-base", "--is-ancestor", revision, "origin/main")
    except RuntimeError as error:
        raise ValueError("Selected revision must exist on fetched protected main. Copy its full commit SHA from GitHub.") from error
    actual = source_version(run("git", "show", f"{revision}:Directory.Build.props"))
    require(actual == selected, f"Selected {selected}, but source defines {actual}. Merge a version-bump PR first.")
    tag = "v" + selected
    require(not any(ref["ref"] == "refs/tags/" + tag for ref in api("git/matching-refs/tags/" + tag)),
            f"Tag {tag} already exists. Released/failed reserved versions cannot be reused.")
    tags = run("gh", "api", "--paginate", f"repos/{REPOSITORY}/releases?per_page=100", "--jq", ".[].tag_name").decode().splitlines()
    require(tag not in tags, f"Release/draft {tag} already exists. Choose a new version.")
    return prerelease


def preflight(selected, revision, namespace):
    prerelease = validate_source(selected, revision)
    for image in image_names(namespace):
        absent_image(f"{image}:{selected}")
    return prerelease


def labels(config, selected, revision, arch):
    require(config["architecture"] == arch and config["os"] == "linux", f"Wrong platform: expected linux/{arch}.")
    actual = config["config"]["Labels"]
    for key, expected in {"version": selected, "revision": revision, "source": SOURCE, "licenses": "MIT"}.items():
        require(actual.get("org.opencontainers.image." + key) == expected, f"Image {arch}: incorrect OCI {key}.")


def export_image(selected, revision, arch):
    """Runs on each native CI runner, only after the comprehensive test suite."""
    require(source_version() == selected, "Source version mismatch before exporting tested image.")
    info = json.loads(run("docker", "image", "inspect", "onvif-webhook-bridge:verify"))[0]
    labels({"architecture": info["Architecture"], "os": info["Os"], "config": info["Config"]}, selected, revision, arch)
    binary = run("docker", "run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
                 "--security-opt", "no-new-privileges", "onvif-webhook-bridge:verify", "--version").decode().strip()
    require(binary == selected, f"Native {arch} executable reports {binary}, expected {selected}.")
    archive = Path(f"image-{arch}.tar")
    run("docker", "save", "onvif-webhook-bridge:verify", "-o", str(archive))
    config_digest, config = saved_config(archive)
    # With Docker's containerd image store, .Id can be an index rather than a config ID.
    require(config["config"] == info["Config"] and config["rootfs"]["diff_ids"] == info["RootFS"]["Layers"],
            "Saved image configuration/layers differ from the tested Docker image.")
    labels(config, selected, revision, arch)
    write_json(f"image-{arch}.json", {"version": selected, "revision": revision, "architecture": arch,
                                      "binaryVersion": binary, "dockerImageId": info["Id"], "configDigest": config_digest,
                                      "archiveDigest": digest_file(archive)})


def saved_config(archive):
    # Read named archive members only; never extract filesystem paths from the archive.
    with tarfile.open(archive) as source:
        manifests = json.load(source.extractfile("manifest.json"))
        require(len(manifests) == 1, "Expected exactly one saved tested image.")
        config = source.extractfile(manifests[0]["Config"]).read()
        return digest(config), json.loads(config)


def digest_file(path):
    with Path(path).open("rb") as stream:
        return "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()


def prepare(selected, revision, directory):
    """Convert the tested archives once; assemble one OCI index for both destinations."""
    layout = Path(directory)
    layout.mkdir()
    manifests = []
    evidence = []
    for arch in ARCHES:
        archive = Path(f"image-{arch}.tar")
        record = json.loads(Path(f"image-{arch}.json").read_text())
        require(all(record[k] == v for k, v in {"version": selected, "revision": revision, "architecture": arch,
                                              "binaryVersion": selected, "archiveDigest": digest_file(archive)}.items()),
                f"Tested artifact evidence mismatch for {arch}.")
        # Separate directories avoid skopeo replacing the previous platform's index.json.
        platform = layout / arch
        # Keep Docker schema 2 configs intact: OCI conversion can strip Docker Healthcheck.
        run("skopeo", "copy", "--format", "v2s2", f"docker-archive:{archive}", f"oci:{platform}:tested")
        descriptor = json.loads((platform / "index.json").read_text())["manifests"][0]
        blob = platform / "blobs" / "sha256" / descriptor["digest"].split(":")[1]
        require(digest(blob.read_bytes()) == descriptor["digest"], "Platform manifest digest mismatch.")
        manifest = json.loads(blob.read_bytes())
        require(manifest["config"]["digest"] == record["configDigest"], "Published image config differs from tested Docker image.")
        config = platform / "blobs" / "sha256" / record["configDigest"].split(":")[1]
        require(digest(config.read_bytes()) == record["configDigest"], "Image config digest mismatch.")
        labels(json.loads(config.read_bytes()), selected, revision, arch)
        destination = layout / "blobs" / "sha256"
        destination.mkdir(parents=True, exist_ok=True)
        for source in (platform / "blobs" / "sha256").iterdir():
            target = destination / source.name
            if not target.exists():
                shutil.copyfile(source, target)
        descriptor.pop("annotations", None)
        descriptor["platform"] = {"os": "linux", "architecture": arch}
        manifests.append(descriptor)
        evidence.append(record)
    index = {"schemaVersion": 2, "mediaType": INDEX_TYPE, "manifests": manifests}
    content = json.dumps(index, separators=(",", ":")).encode()
    expected = digest(content)
    (layout / "blobs" / "sha256" / expected.split(":")[1]).write_bytes(content)
    write_json(layout / "oci-layout", {"imageLayoutVersion": "1.0.0"})
    write_json(layout / "index.json", {"schemaVersion": 2, "manifests": [
        {"mediaType": INDEX_TYPE, "digest": expected, "size": len(content),
         "annotations": {"org.opencontainers.image.ref.name": "release"}}]})
    result = {"version": selected, "tag": "v" + selected, "revision": revision, "indexDigest": expected,
              "manifests": manifests, "testedArtifacts": evidence}
    write_json(layout / "release.json", result)
    return result


def verify_registry(image, tag, expected):
    raw = run("skopeo", "inspect", "--raw", f"docker://{image}:{tag}")
    # skopeo prints the raw manifest bytes without re-serializing them.
    require(digest(raw) == expected["indexDigest"], f"Registry index digest mismatch: {image}:{tag}.")
    index = json.loads(raw)
    require(index["manifests"] == expected["manifests"], f"Registry platform manifests mismatch: {image}:{tag}.")
    for manifest in index["manifests"]:
        reference = f"docker://{image}@{manifest['digest']}"
        raw_manifest = run("skopeo", "inspect", "--raw", reference)
        require(digest(raw_manifest) == manifest["digest"], "Remote platform digest mismatch.")
        config = json.loads(run("skopeo", "inspect", "--config", reference))
        labels(config, expected["version"], expected["revision"], manifest["platform"]["architecture"])


def publish(selected, revision, namespace, directory):
    # Recheck after the long CI gate and any environment approval, before the first write.
    prerelease = preflight(selected, revision, namespace)
    require(run("git", "rev-parse", "HEAD").decode().strip() == revision, "Publisher checkout is not the selected commit.")
    expected = prepare(selected, revision, directory)
    images = image_names(namespace)
    tag = "v" + selected
    # An atomic create (never an update) reserves this version. Subsequent failures burn the version.
    api("git/refs", {"ref": "refs/tags/" + tag, "sha": revision})
    notes = api("releases/generate-notes", {"tag_name": tag, "target_commitish": revision})["body"]
    installation = "\n\n## Installation\n\n" + "\n".join(f"- `{image}:{selected}@{expected['indexDigest']}`" for image in images)
    installation += f"\n\nSupports linux/amd64 and linux/arm64. [Setup and upgrades]({SOURCE}/blob/{tag}/docs/DEPLOYMENT.md).\n"
    release = api("releases", {"tag_name": tag, "target_commitish": revision, "name": tag,
                               "draft": True, "prerelease": prerelease, "body": notes + installation})
    run("gh", "release", "upload", tag, str(Path(directory) / "release.json"), "--repo", REPOSITORY)
    for image in images:
        absent_image(f"{image}:{selected}")
        run("skopeo", "copy", "--all", "--preserve-digests", f"oci:{directory}:release", f"docker://{image}:{selected}")
        verify_registry(image, selected, expected)
    # Stable aliases move only after BOTH version manifests have been published and verified.
    if not prerelease:
        for image in images:
            run("skopeo", "copy", "--all", "--preserve-digests", f"docker://{image}@{expected['indexDigest']}", f"docker://{image}:latest")
            verify_registry(image, "latest", expected)
    ref = api("git/ref/tags/" + tag)
    require(ref["object"]["type"] == "commit" and ref["object"]["sha"] == revision, "Release tag moved during publication.")
    final = api(f"releases/{release['id']}", {"draft": False, "make_latest": "false" if prerelease else "true"}, "PATCH")
    require(final["tag_name"] == tag and final["prerelease"] == prerelease and not final["draft"], "GitHub Release state mismatch.")
    print(f"Released {tag} from {revision}; both registries: {expected['indexDigest']}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("version", "preflight", "export", "publish"))
    parser.add_argument("--version", dest="selected", default="")
    parser.add_argument("--revision", default="")
    parser.add_argument("--namespace", default="")
    parser.add_argument("--arch", choices=ARCHES)
    parser.add_argument("--directory", default="release-oci")
    args = parser.parse_args()
    if args.command == "version":
        actual = source_version()
        require(not args.selected or actual == args.selected, f"Source version {actual} differs from requested {args.selected}.")
        print(actual)
    elif args.command == "preflight":
        preflight(args.selected, args.revision, args.namespace)
    elif args.command == "export":
        export_image(args.selected, args.revision, args.arch)
    else:
        publish(args.selected, args.revision, args.namespace, args.directory)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, KeyError) as error:
        print(f"Release stopped: {error}", file=sys.stderr)
        sys.exit(1)
