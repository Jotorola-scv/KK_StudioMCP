"""Real Studio integration: MCP requests only; never mouse, keyboard, or desktop capture."""
import argparse
import base64
import json
from pathlib import Path
from mcp_client import McpClient


def run(stage, resume=False):
    output = Path(".local/evidence/mvp-" + stage).resolve()
    client = McpClient("src/StudioMcp.Host/bin/Release/net8.0/StudioMcp.Host.dll", output)
    count = 0

    def call(name, arguments=None):
        nonlocal count
        response = client.request("tools/call", {"name": name, "arguments": arguments or {}}, timeout=120)
        count += 1
        (output / f"{count:02d}-{name}.json").write_text(json.dumps(response, indent=2), encoding="utf-8")
        if response.get("isError"):
            raise RuntimeError(response)
        text = next((x["text"] for x in response.get("content", []) if x["type"] == "text"), "{}")
        payload = json.loads(text)
        return payload.get("result", payload), response

    try:
        status, _ = call("studio_status")
        assert status["ready"] and status["writesEnabled"] and status["materialEditor"] and status["screencap"]
        session = status["sceneSession"]
        if stage == "initial" and not resume:
            scene, _ = call("scene_snapshot")
            assert scene["total"] == 0, "Use a fresh test Studio; do not clear an existing scene automatically."
            item, _ = call("add_item", {"sceneSession": session, "group": 0, "category": 0,
                "itemId": 0, "name": "StudioMCP test sphere", "position": [0, 0.5, 0], "scale": [8, 8, 8]})
            materials, _ = call("materials_list", {"objectId": item["id"]})
            target = {"objectId": item["id"], "rendererId": materials[0]["rendererId"], "slot": materials[0]["slot"]}
            inspected, _ = call("material_inspect", target)
            props = inspected["properties"]
            color = next((x for x in props if x["name"] == "_Color"), None) or next(x for x in props if x["kind"] == "color")
            number = next((x for x in props if x["kind"] in ("float", "range") and "Gloss" in x["name"]), None) \
                or next((x for x in props if x["kind"] in ("float", "range")), None)
            state = {"sceneSession": session, "target": target, "color": color, "number": number}
            Path(".local/evidence/mvp-state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
            color_value = [0.85, 0.12, 0.08, 1]
        else:
            state = json.loads(Path(".local/evidence/mvp-state.json").read_text(encoding="utf-8"))
            assert state["sceneSession"] == session, "Scene session changed; do not reuse old object handles."
            target = state["target"]
            color = state["color"]
            number = state["number"]
            color_value = [0.85, 0.12, 0.08, 1] if stage == "initial" else [0.08, 0.28, 0.85, 1]
        if number is None:
            inspected, _ = call("material_inspect", target)
            number = next((x for x in inspected["properties"] if x["kind"] in ("float", "range")), None)
            assert number is not None, "The MVP must verify a float write as well as a color write."
            state["number"] = number
            Path(".local/evidence/mvp-state.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
        changed, _ = call("material_set", {**target, "sceneSession": session,
            "property": color["name"], "kind": "color", "value": color_value})
        assert all(abs(a-b) < 1e-5 for a, b in zip(changed["after"], color_value)), changed
        if number:
            value = 0.35 if stage == "initial" else 0.7
            changed_number, _ = call("material_set", {**target, "sceneSession": session,
                "property": number["name"], "kind": "float", "value": value})
            assert abs(changed_number["after"]-value) < 1e-5, changed_number
        camera, _ = call("camera_set", {"sceneSession": session, "pivot": [0, 0.5, 0],
            "rotation": [15, 180, 0], "fov": 30 if stage == "initial" else 26})
        assert camera["fov"] == (30 if stage == "initial" else 26)
        captured, response = call("capture", {"sceneSession": session, "width": 640, "height": 360})
        assert captured["state"] == "complete", captured
        images = [x for x in response["content"] if x["type"] == "image"]
        assert images, "Capture must return actual MCP image content, not only a path."
        image_path = output / "preview.png"
        image_path.write_bytes(base64.b64decode(images[0]["data"]))
        print(json.dumps({"stage": stage, "sceneSession": session, "target": target,
            "colorProperty": color["name"], "floatProperty": number["name"] if number else None,
            "color": color_value, "capture": captured, "preview": str(image_path)}, indent=2))
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("stage", choices=["initial", "followup"])
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    run(args.stage, args.resume)
