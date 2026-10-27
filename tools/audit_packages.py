"""Read-only RC archive audit. Does not load assemblies, connect to Studio or extract archives."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import zipfile


def audit(directory, expected_commit):
    directory = Path(directory)
    checksums = {}
    for line in (directory / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines():
        digest, name = line.split("  ", 1)
        if not re.fullmatch(r"[0-9a-f]{64}", digest) or Path(name).name != name:
            raise ValueError("Invalid checksum entry")
        if name in checksums:
            raise ValueError("Duplicate checksum entry")
        checksums[name] = digest
    if len(checksums) != 3 or {p.name for p in directory.glob("*.zip")} != set(checksums):
        raise ValueError("Expected exactly the Host/KKS/KK release archives")
    results = []
    components = set()
    for name, digest in checksums.items():
        path = directory / name
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError("Archive checksum mismatch: " + name)
        with zipfile.ZipFile(path) as archive:
            entries = [x for x in archive.infolist() if not x.is_dir()]
            names = [x.filename for x in entries]
            if len(names) != len(set(names)) or archive.testzip() is not None:
                raise ValueError("Duplicate or corrupt ZIP member")
            for item in entries:
                relative = PurePosixPath(item.filename)
                if relative.is_absolute() or ".." in relative.parts or "\\" in item.filename or ":" in item.filename:
                    raise ValueError("Unsafe archive path")
                if item.file_size > 64 * 1024 * 1024:
                    raise ValueError("Unexpectedly large package member")
                if re.search(r"(?i)(^|/)(connection\.json|\.local|bin|obj|Evidence|Reports|TestResults)(/|$)|\.(pdb|log|raw|dmp)$", item.filename):
                    raise ValueError("Private/generated payload: " + item.filename)
                if item.filename.endswith((".md", ".json", ".yaml", ".toml")):
                    text = archive.read(item).decode("utf-8-sig")
                    if re.search(r"[\u3400-\u9fff]|(?i:C:[/\\]Users|D:[/\\]Document|gh[pousr]_[A-Za-z0-9_]{20,})", text):
                        raise ValueError("Nonportable document or private data")
            manifest = json.loads(archive.read("PACKAGE-MANIFEST.json"))
            if manifest["sourceCommit"] != expected_commit or manifest["version"] != "0.1.0-rc1":
                raise ValueError("Wrong source/version provenance")
            component = manifest["component"]
            if component not in {"Host", "KK", "KKS"} or component in components:
                raise ValueError("Wrong/duplicate component")
            components.add(component)
            expected_files = {x["path"]: x for x in manifest["files"]}
            if len(expected_files) != len(manifest["files"]) or set(names) != set(expected_files) | {"PACKAGE-MANIFEST.json"}:
                raise ValueError("Manifest inventory mismatch")
            for member, expected in expected_files.items():
                data = archive.read(member)
                if len(data) != expected["bytes"] or hashlib.sha256(data).hexdigest() != expected["sha256"]:
                    raise ValueError("Payload hash mismatch: " + member)
            required = {"LICENSE", "INSTALL.md", "ACCEPTANCE.md", "RELEASE-NOTES.md", "THIRD-PARTY-NOTICES.md", "licenses/nuget-packages.json"}
            if not required <= set(names):
                raise ValueError("Missing delivery documentation/notices")
            dlls = {x for x in names if x.lower().endswith(".dll")}
            if component == "Host":
                deps = json.loads(archive.read("Host/StudioMcp.Host.deps.json"))
                config = json.loads(archive.read("Host/StudioMcp.Host.runtimeconfig.json"))
                if config["runtimeOptions"]["tfm"] != "net8.0":
                    raise ValueError("Wrong host runtime")
                if not any(x.startswith("StudioMcp.Host/0.1.0-rc1") for x in deps["libraries"]):
                    raise ValueError("Wrong actual host version")
                allowed = {"StudioMcp.Host.dll"}
                for target in deps["targets"].values():
                    for library in target.values():
                        for key in ("runtime", "runtimeTargets"):
                            allowed.update(PurePosixPath(x).name for x in library.get(key, {}) if x.endswith(".dll"))
                if "Host/StudioMcp.Host.dll" not in dlls or any(not x.startswith("Host/") or PurePosixPath(x).name not in allowed for x in dlls):
                    raise ValueError("Incomplete or unexpected host assemblies")
                # Each non-RID runtime assembly advertised in deps must be present.
                for target in deps["targets"].values():
                    for library in target.values():
                        for member in library.get("runtime", {}):
                            if member.endswith(".dll") and "Host/" + PurePosixPath(member).name not in names:
                                raise ValueError("Missing host dependency: " + member)
                if "skills/studiomcp-shader-authoring/SKILL.md" not in names:
                    raise ValueError("Missing portable authoring skill")
            else:
                prefix = "BepInEx/plugins/StudioMCP/"
                required_dlls = {prefix + component + "_StudioMCP.dll", prefix + "Newtonsoft.Json.dll", prefix + "websocket-sharp.dll"}
                if dlls != required_dlls:
                    raise ValueError("Wrong game bridge/dependency set")
            results.append({"component": component, "archive": name, "files": len(names), "sha256": digest})
    return {"status": "passed", "sourceCommit": expected_commit, "packages": results,
            "scope": "Archive structure, exact payload/provenance/dependency checks only; no game acceptance"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", required=True)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    print(json.dumps(audit(args.assets, args.commit), indent=2))
