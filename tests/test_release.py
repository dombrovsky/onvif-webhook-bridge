"""Offline release scenarios: all external commands are faked; no registry writes."""
import importlib.util
from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import tempfile
import tarfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("release", Path(__file__).resolve().parents[1] / "build/release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
SHA = "a" * 40
VERSION = "0.1.0-rc.1"


class ReleaseTests(unittest.TestCase):
    def test_semver_and_prereleases(self):
        for value in (VERSION, "0.1.0-rc.2", "0.1.0-alpha.0", "1.0.0-beta-2"):
            self.assertTrue(release.version(value))
        for value in ("0.1.0", "0.2.0", "1.0.0"):
            self.assertFalse(release.version(value))
        for value in ("v0.1.0", "01.1.0", "0.1", "0.1.0-rc.01", "0.1.0-", "0.1.0+build", "0.1.0\n", "$(bad)", "65535.0.0", "0.1.0-" + "a" * 128):
            with self.subTest(value=value), self.assertRaises(ValueError):
                release.version(value)

    def test_authoritative_source(self):
        release.version(release.source_version())
        self.assertEqual(VERSION, release.source_version("<Project><VersionPrefix>0.1.0</VersionPrefix><VersionSuffix>rc.1</VersionSuffix></Project>"))
        self.assertEqual("1.0.0", release.source_version("<Project><VersionPrefix>1.0.0</VersionPrefix><VersionSuffix /></Project>"))
        for xml in ("<Project />", '<Project><VersionPrefix Condition="x">1.0.0</VersionPrefix><VersionSuffix /></Project>'):
            with self.assertRaises(ValueError):
                release.source_version(xml)

    def test_candidate_counter_and_local_fallback(self):
        xml = "<Project><VersionPrefix>0.1.0</VersionPrefix><VersionSuffix>rc.$(RcNumber)</VersionSuffix></Project>"
        for number, expected in (("", "1"), ("42", "42"), ("43", "43")):
            with patch.dict(os.environ, GITHUB_RUN_NUMBER=number):
                self.assertEqual("0.1.0-rc." + expected, release.source_version(xml))
                with patch.object(release, "run", return_value=xml.encode()):
                    self.assertEqual("0.1.0-rc." + expected, release.revision_version(SHA))
                self.assertEqual("1.0.0", release.source_version("<Project><VersionPrefix>1.0.0</VersionPrefix><VersionSuffix /></Project>"))
        for number in ("0", "01", "-1", "bad"):
            with patch.dict(os.environ, GITHUB_RUN_NUMBER=number), self.assertRaises(ValueError):
                release.source_version(xml)

    def test_registry_absence_fails_closed(self):
        for message in ("unauthorized", "denied", "timeout", "404 gateway error"):
            with patch.object(release, "run", side_effect=RuntimeError(message)), self.assertRaises(ValueError):
                release.absent_image("synthetic:1.0.0")
        with patch.object(release, "run", side_effect=RuntimeError("manifest unknown")):
            release.absent_image("synthetic:1.0.0")
        with patch.object(release, "run", return_value=b"{}"), self.assertRaises(ValueError):
            release.absent_image("synthetic:1.0.0")

    def test_release_version_comes_from_selected_commit_without_version_input(self):
        for suffix, expected in (("rc.2", "0.2.0-rc.2"), ("", "0.2.0")):
            xml = f"<Project><VersionPrefix>0.2.0</VersionPrefix><VersionSuffix>{suffix}</VersionSuffix></Project>".encode()
            output = io.StringIO()
            with patch.object(release.sys, "argv", ["release.py", "version", "--revision", SHA]), patch.object(release, "run", return_value=xml) as command, redirect_stdout(output):
                release.main()
            self.assertEqual(expected, output.getvalue().strip())
            command.assert_called_once_with("git", "show", f"{SHA}:Directory.Build.props")

    def test_version_resolution_rejects_non_commit_inputs_before_reading_source(self):
        for revision in ("main", "refs/heads/main", "a" * 39, SHA + ":other-file", ""):
            with patch.object(release, "run") as command, self.assertRaises(ValueError):
                release.revision_version(revision)
            command.assert_not_called()

    def test_main_revision_protection_version_and_tag_guards(self):
        def api(path):
            return {"protected": True} if path == "branches/main" else []

        def command(*args, **kwargs):
            if args[:2] == ("git", "show"):
                return b"<Project><VersionPrefix>0.1.0</VersionPrefix><VersionSuffix>rc.1</VersionSuffix></Project>"
            return b""

        with patch.dict(os.environ, GITHUB_REF="refs/heads/main", GITHUB_REPOSITORY=release.REPOSITORY), patch.object(release, "run", side_effect=command), patch.object(release, "api", side_effect=api):
            self.assertTrue(release.validate_source(VERSION, SHA))
            for selected, revision in (("0.1.0-rc.2", SHA), (VERSION, "main"), (VERSION, "a" * 39)):
                with self.assertRaises(ValueError):
                    release.validate_source(selected, revision)
            with patch.dict(os.environ, GITHUB_REF="refs/heads/feature"), self.assertRaises(ValueError):
                release.validate_source(VERSION, SHA)
            with patch.object(release, "api", return_value={"protected": False}), self.assertRaises(ValueError):
                release.validate_source(VERSION, SHA)
            with patch.object(release, "api", side_effect=[{"protected": True}, [{"ref": "refs/tags/v" + VERSION}]]), self.assertRaises(ValueError):
                release.validate_source(VERSION, SHA)
            with patch.object(release, "run", side_effect=RuntimeError("not an ancestor")), self.assertRaisesRegex(ValueError, "protected main"):
                release.validate_source(VERSION, SHA)
            with patch.object(release, "run", side_effect=lambda *a, **k: ("v" + VERSION).encode() if a[0] == "gh" else command(*a)), self.assertRaises(ValueError):
                release.validate_source(VERSION, SHA)

    def config(self, arch, selected=VERSION):
        return {"architecture": arch, "os": "linux", "config": {"Labels": {
            "org.opencontainers.image." + k: v for k, v in
            {"version": selected, "revision": SHA, "source": release.SOURCE, "licenses": "MIT"}.items()}}}

    def test_version_labels_platforms_and_revision_must_match(self):
        for arch in release.ARCHES:
            config = self.config(arch)
            release.labels(config, VERSION, SHA, arch)
            for selected, revision, target in (("0.1.0", SHA, arch), (VERSION, "b" * 40, arch), (VERSION, SHA, "wrong")):
                with self.assertRaises(ValueError):
                    release.labels(config, selected, revision, target)

    def test_native_binary_mismatch_rejected_before_export(self):
        config = self.config("amd64")
        info = {"Architecture": "amd64", "Os": "linux", "Config": config["config"]}
        with patch.object(release, "source_version", return_value=VERSION), patch.object(release, "run", side_effect=[json.dumps([info]).encode(), b"0.1.0\n"]), self.assertRaisesRegex(ValueError, "Native amd64 executable"):
            release.export_image(VERSION, SHA, "amd64")

    def test_docker_index_id_is_not_used_as_config_digest_and_healthcheck_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            previous = os.getcwd()
            try:
                os.chdir(directory)
                config = self.config("amd64")
                config["config"]["Healthcheck"] = {"Test": ["CMD", "./OnvifWebhookBridge", "--health-probe"]}
                config["rootfs"] = {"diff_ids": ["sha256:" + "d" * 64]}
                content = json.dumps(config).encode()
                with tarfile.open("image-amd64.tar", "w") as archive:
                    for name, value in (("manifest.json", json.dumps([{"Config": "config.json"}]).encode()), ("config.json", content)):
                        header = tarfile.TarInfo(name)
                        header.size = len(value)
                        archive.addfile(header, io.BytesIO(value))
                for classic in (False, True):
                    inspected = dict(config["config"])
                    if classic:
                        # The classic daemon synthesizes defaults in inspect, without changing the config blob.
                        inspected.update(AttachStdin=False, AttachStdout=False, AttachStderr=False, Tty=False)
                    info = {"Architecture": "amd64", "Os": "linux", "Config": inspected,
                            "RootFS": {"Layers": config["rootfs"]["diff_ids"]},
                            "Id": release.digest(content) if classic else "sha256:" + "e" * 64}
                    with patch.object(release, "source_version", return_value=VERSION), patch.object(release, "run", side_effect=[json.dumps([info]).encode(), VERSION.encode(), b""]):
                        release.export_image(VERSION, SHA, "amd64")
                    evidence = json.loads(Path("image-amd64.json").read_text())
                    self.assertEqual(release.digest(content), evidence["configDigest"])
                    self.assertEqual(classic, evidence["dockerImageId"] == evidence["configDigest"])
                    info["RootFS"]["Layers"] = ["sha256:" + "f" * 64]
                    with patch.object(release, "source_version", return_value=VERSION), patch.object(release, "run", side_effect=[json.dumps([info]).encode(), VERSION.encode(), b""]), self.assertRaisesRegex(ValueError, "Saved image layers"):
                        release.export_image(VERSION, SHA, "amd64")
                self.assertIn("Healthcheck", release.saved_config("image-amd64.tar")[1]["config"])
            finally:
                os.chdir(previous)

    def fixture(self):
        for arch in release.ARCHES:
            archive = Path(f"image-{arch}.tar")
            archive.write_bytes(b"synthetic tested archive " + arch.encode())
            config = json.dumps(self.config(arch)).encode()
            release.write_json(f"image-{arch}.json", {"architecture": arch, "version": VERSION, "revision": SHA,
                "binaryVersion": VERSION, "archiveDigest": release.digest_file(archive), "configDigest": release.digest(config)})

    def convert(self, *args, **kwargs):
        self.assertEqual(("--format", "v2s2"), args[2:4])
        arch = "amd64" if "amd64" in args[-1] else "arm64"
        target = Path(args[-1].removeprefix("oci:").removesuffix(":tested"))
        blobs = target / "blobs/sha256"
        blobs.mkdir(parents=True)
        config = json.dumps(self.config(arch)).encode()
        config_digest = release.digest(config)
        (blobs / config_digest.split(":")[1]).write_bytes(config)
        manifest = json.dumps({"schemaVersion": 2, "config": {"digest": config_digest}}).encode()
        manifest_digest = release.digest(manifest)
        (blobs / manifest_digest.split(":")[1]).write_bytes(manifest)
        release.write_json(target / "index.json", {"manifests": [{"digest": manifest_digest, "size": len(manifest), "mediaType": "application/vnd.oci.image.manifest.v1+json"}]})
        return b""

    def test_same_tested_configs_assemble_one_index_and_detect_tampering(self):
        with tempfile.TemporaryDirectory() as directory:
            previous = os.getcwd()
            try:
                os.chdir(directory)
                self.fixture()
                with patch.object(release, "run", side_effect=self.convert):
                    evidence = release.prepare(VERSION, SHA, "layout")
                self.assertEqual(["amd64", "arm64"], [m["platform"]["architecture"] for m in evidence["manifests"]])
                self.assertEqual(evidence["indexDigest"], release.digest((Path("layout/blobs/sha256") / evidence["indexDigest"].split(":")[1]).read_bytes()))
                Path("image-amd64.tar").write_bytes(b"tampered")
                with self.assertRaisesRegex(ValueError, "evidence mismatch"):
                    release.prepare(VERSION, SHA, "tampered")
            finally:
                os.chdir(previous)

    def test_remote_index_mismatch(self):
        with patch.object(release, "run", return_value=b"{}"), self.assertRaisesRegex(ValueError, "index digest mismatch"):
            release.verify_registry("synthetic", VERSION, {"indexDigest": "sha256:" + "0" * 64})

    def scenario(self, selected, fail_image=None, fail_alias=False):
        events = []
        expected = {"indexDigest": "sha256:" + "c" * 64}

        def command(*args, **kwargs):
            events.append(args)
            if args[:2] == ("git", "rev-parse"):
                return SHA.encode()
            if args[:2] == ("skopeo", "copy"):
                if fail_image and fail_image in args[-1] and (not fail_alias or args[-1].endswith(":latest")):
                    raise RuntimeError("synthetic registry failure")
            return b""

        def api(path, payload=None, method=None):
            events.append((path, payload))
            if path == "releases/generate-notes":
                return {"body": "Synthetic notes"}
            if path == "releases":
                return {"id": 42}
            if path.startswith("git/ref/"):
                return {"object": {"type": "commit", "sha": SHA}}
            return {"tag_name": "v" + selected, "prerelease": "-" in selected, "draft": False}

        with patch.object(release, "run", side_effect=command), patch.object(release, "api", side_effect=api), patch.object(release, "preflight", return_value="-" in selected), patch.object(release, "prepare", return_value=expected), patch.object(release, "absent_image"), patch.object(release, "verify_registry") as verify:
            if fail_image:
                with self.assertRaises(RuntimeError):
                    release.publish(selected, SHA, "synthetic", "layout")
            else:
                release.publish(selected, SHA, "synthetic", "layout")
                self.assertEqual(2 if "-" in selected else 4, verify.call_count)
        return events

    def test_prerelease_never_moves_latest_and_finalizes_last(self):
        events = self.scenario(VERSION)
        self.assertFalse(any(":latest" in str(e) for e in events))
        self.assertEqual("releases/42", events[-1][0])
        self.assertTrue(next(e[1] for e in events if e[0] == "releases")["draft"])

    def test_stable_aliases_follow_both_immutable_publications(self):
        events = self.scenario("0.1.0")
        copies = [e[-1] for e in events if e[:2] == ("skopeo", "copy")]
        self.assertEqual([":0.1.0", ":0.1.0", ":latest", ":latest"], [":" + e.rsplit(":", 1)[1] for e in copies])
        self.assertEqual("true", events[-1][1]["make_latest"])

    def test_second_registry_failure_keeps_draft_and_aliases_untouched(self):
        events = self.scenario("0.1.0", "docker.io/synthetic")
        self.assertFalse(any(e[0] == "releases/42" for e in events))
        self.assertFalse(any(":latest" in str(e) for e in events))

    def test_alias_failure_does_not_advertise_success(self):
        events = self.scenario("0.1.0", "docker.io/synthetic", fail_alias=True)
        self.assertFalse(any(e[0] == "releases/42" for e in events))


if __name__ == "__main__":
    unittest.main()
