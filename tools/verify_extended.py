"""Live integration against an empty, disposable Studio, exclusively via MCP tools."""
import argparse
import base64
import json
import os
from pathlib import Path
import time
from PIL import Image
from mcp_client import McpClient


def run(game, card):
    root = Path("D:/Program Files/" + ("KoikatuSunshine" if game == "KKS" else "Koikatu"))
    os.environ["STUDIOMCP_CONNECTION"] = str(root / "UserData/StudioMCP/connection.json")
    output = Path(".local/evidence/extended-" + game.lower()).resolve()
    client = McpClient("src/StudioMcp.Host/bin/Release/net8.0/StudioMcp.Host.dll", output)
    counter = 0

    def call(name, args=None, fail=False):
        nonlocal counter
        response = client.request("tools/call", {"name": name, "arguments": args or {}}, timeout=150)
        counter += 1
        (output / f"{counter:02d}-{name}.json").write_text(json.dumps(response, indent=2), encoding="utf-8")
        if fail:
            assert response.get("isError"), response
            return None
        if response.get("isError"):
            raise RuntimeError(response)
        payload = json.loads(next(x["text"] for x in response["content"] if x["type"] == "text"))
        return payload.get("result", payload)

    def inspect(target):
        return call("material_inspect", target)

    def value(target, prop):
        return next(p["value"] for p in inspect(target)["properties"] if p["name"] == prop)

    def target_for(material):
        return {k: material[k] for k in ("objectId", "rendererId", "slot")}

    def close(a, b):
        if isinstance(a, list):
            assert len(a) == len(b) and all(abs(x-y) < 1e-4 for x, y in zip(a, b)), (a, b)
        else:
            assert abs(a-b) < 1e-4, (a, b)

    try:
        deadline = time.monotonic() + 90
        while True:
            try:
                status = call("studio_status")
                if status["ready"] and status["materialEditor"] and status["screencap"]:
                    break
            except RuntimeError:
                if time.monotonic() >= deadline:
                    raise
            if time.monotonic() >= deadline:
                raise TimeoutError("Studio providers did not become ready.")
            time.sleep(2)
        assert status["ready"] and status["writesEnabled"] and status["materialEditor"] and status["screencap"]
        session = status["sceneSession"]
        assert call("scene_snapshot")["total"] == 0, "Refuse to clear a nonempty scene. Start a disposable Studio."
        sphere = call("add_item", {"sceneSession": session, "group": 0, "category": 0, "itemId": 0,
            "name": "StudioMCP integration sphere", "position": [0, .5, 0], "scale": [8, 8, 8]})
        target = target_for(call("materials_list", {"objectId": sphere["id"]})[0])
        props = inspect(target)["properties"]
        color = next(p for p in props if p["kind"] == "color")
        number = next(p for p in props if p["kind"] in ("float", "range"))
        texture = next(p for p in props if p["name"] == "_MainTex")
        render = call("render_get")
        operations = [
            {"method": "me.set", "args": {**target, "property": color["name"], "kind": "color", "value": [.1, .35, .8, 1]}},
            {"method": "me.set", "args": {**target, "property": number["name"], "kind": "float", "value": .42}},
            {"method": "object.set_transform", "args": {"objectId": sphere["id"], "position": [.2, .5, 0]}},
            {"method": "camera.set", "args": {"pivot": [0, .5, 0], "rotation": [15, 180, 0], "fov": 28}},
            {"method": "render.set", "args": {"values": {"shadowDistance": 77, "fogDensity": .012}}},
        ]
        bad = [operations[2], {"method": "camera.set", "args": {"fov": -5}}]
        call("batch_apply", {"sceneSession": session, "operations": bad}, fail=True)
        close(call("scene_snapshot")["objects"][0]["position"], [0, .5, 0])
        batch = call("batch_apply", {"sceneSession": session, "operations": operations})
        assert batch["undoAvailable"], batch
        close(value(target, number["name"]), .42)
        close(call("render_get")["shadowDistance"], 77)
        assert call("batch_undo", {"sceneSession": session, "batchId": batch["batchId"]})["restored"]
        close(value(target, color["name"]), color["value"])
        close(value(target, number["name"]), number["value"])
        close(call("render_get")["shadowDistance"], render["shadowDistance"])
        batch = call("batch_apply", {"sceneSession": session, "operations": operations})
        fixture = output / "checker.png"
        image = Image.new("RGB", (64, 64))
        image.putdata([(240, 240, 240) if (x//8+y//8) % 2 else (80, 100, 160) for y in range(64) for x in range(64)])
        image.save(fixture)
        imported = call("material_import_texture", {**target, "sceneSession": session, "property": texture["name"], "path": str(fixture)})
        assert imported["after"]["width"] == 64 and imported["after"]["height"] == 64, imported
        # The source card is never rewritten; the character stays far outside the capture camera.
        scopes = {}
        cards = [Path(card).resolve()] + [p for p in Path(card).parent.glob("*.png") if p != Path(card)][:5]
        characters = []
        for card_path in cards:
            char_name = "StudioMCP test character " + str(len(characters))
            character = call("add_character", {"sceneSession": session, "path": str(card_path),
                "name": char_name, "position": [1000 + len(characters)*10, 0, 0]})
            characters.append(character)
            time.sleep(3)  # Installed character mods may rebuild renderers after Add returns.
            for material in call("materials_list", {"objectId": character["id"]}):
                if material["scope"] not in scopes:
                    candidate = target_for(material)
                    color_property = next((p for p in inspect(candidate)["properties"] if p["kind"] == "color"), None)
                    if color_property:
                        scopes[material["scope"]] = {"target": candidate, "property": color_property["name"],
                            "before": color_property["value"], "name": material["name"], "characterPosition": character["position"]}
            if {"Character", "Hair", "Clothing", "Accessory"}.issubset(scopes):
                break
        assert {"Character", "Hair", "Clothing", "Accessory"}.issubset(scopes), scopes.keys()
        for scope, data in scopes.items():
            edited = call("material_set", {**data["target"], "sceneSession": session,
                "property": data["property"], "kind": "color", "value": [.27, .38, .49, 1]})
            close(edited["after"], [.27, .38, .49, 1])
            assert edited["scope"] == scope
        clothing = scopes["Clothing"]
        imported_char = call("material_import_texture", {**clothing["target"], "sceneSession": session,
            "property": "_MainTex", "path": str(fixture)})
        assert imported_char["after"]["width"] == 64, imported_char
        checkpoint = call("scene_checkpoint", {"sceneSession": session})
        assert Path(checkpoint["path"]).stat().st_size > 1000
        call("material_reset", {**target, "sceneSession": session, "property": texture["name"], "kind": "texture"})
        call("material_set", {**target, "sceneSession": session, "property": color["name"], "kind": "color", "value": [1, 0, 0, 1]})
        call("render_set", {"sceneSession": session, "values": {"shadowDistance": 15}})
        restored = call("scene_restore", {"sceneSession": session, "path": checkpoint["path"]})
        time.sleep(3)
        new_status = call("studio_status")
        new_session = new_status["sceneSession"]
        assert new_session != session, restored
        call("camera_set", {"sceneSession": session, "fov": 30}, fail=True)
        scene = call("scene_snapshot")
        assert scene["total"] == 1 + len(characters), scene
        sphere = next(o for o in scene["objects"] if o["name"] == "StudioMCP integration sphere")
        target = target_for(call("materials_list", {"objectId": sphere["id"]})[0])
        close(value(target, color["name"]), [.1, .35, .8, 1])
        close(value(target, number["name"]), .42)
        assert value(target, texture["name"])["width"] == 64, "Texture must persist after save and load."
        close(call("render_get")["shadowDistance"], 77)
        for scope, data in scopes.items():
            char = next(o for o in scene["objects"] if o["type"].startswith("OCIChar")
                and abs(o["position"][0] - data["characterPosition"][0]) < .01)
            reloaded = call("materials_list", {"objectId": char["id"]})
            material = next(m for m in reloaded if m["scope"] == scope and m["name"] == data["name"])
            close(value(target_for(material), data["property"]), [.27, .38, .49, 1])
            if scope == "Clothing":
                assert value(target_for(material), "_MainTex")["width"] == 64
        captured = call("capture", {"sceneSession": new_session, "width": 3840, "height": 2160})
        with Image.open(captured["path"]) as final:
            assert final.size == (3840, 2160), final.size
            final.thumbnail((960, 540))
            final.save(output / "highres-preview.png")
        assert not call("studio_status")["captureBusy"]
        summary = {"game": game, "scopes": list(scopes), "checkpoint": checkpoint,
            "capture": captured, "passed": ["scene", "ME Float/Color", "prevalidation", "batch undo", "native render", "texture import", "character scopes", "save/load persistence", "stale session", "4K Screencap"]}
        (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
        print(json.dumps(summary, indent=2))
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--game", choices=["KK", "KKS"], required=True)
    parser.add_argument("--card", required=True)
    args = parser.parse_args()
    run(args.game, args.card)
