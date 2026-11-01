"""Verify character/KKPE/Timeline tools in an empty, disposable Studio via stdio MCP.

Requires installed KKPE and Timeline. Never clears an existing scene or writes the source card.
Raw responses and renders belong in an ignored, machine-local output directory.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import time
from mcp_client import McpClient


def run(root, card, sex, server, output):
    root, card, output = Path(root).resolve(), Path(card).resolve(), Path(output).resolve()
    original_hash = hashlib.sha256(card.read_bytes()).hexdigest()
    output.mkdir(parents=True, exist_ok=False)
    fixture = output / "character.png"
    shutil.copyfile(card, fixture)
    os.environ["STUDIOMCP_CONNECTION"] = str(root / "UserData/StudioMCP/connection.json")
    os.environ["STUDIOMCP_REQUEST_TIMEOUT_SECONDS"] = "180"
    client = McpClient(server, output)
    count, passed = 0, []

    def call(name, args=None, fail=False):
        nonlocal count
        result = client.request("tools/call", {"name": name, "arguments": args or {}}, timeout=185)
        count += 1
        (output / f"{count:03d}-{name}.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        if fail:
            assert result.get("isError"), (name, result)
            return
        if result.get("isError"):
            raise RuntimeError(f"{name}: {result}")
        for item in result.get("content", []):
            if item["type"] == "image":
                (output / "pose-preview.png").write_bytes(base64.b64decode(item["data"]))
        payload = json.loads(next(item["text"] for item in result["content"] if item["type"] == "text"))
        return payload.get("result", payload)

    def close(actual, expected, tolerance=0.001):
        if isinstance(expected, list):
            assert len(actual) == len(expected), (actual, expected)
            for a, b in zip(actual, expected):
                close(a, b, tolerance)
        else:
            assert abs(actual - expected) < tolerance, (actual, expected)

    def write(tool, **args):
        return call(tool, {"sceneSession": session, **args})

    def keyed(kind, name, a, b, **binding):
        track = write("timeline_track_create", objectId=actor_id, kind=kind, name=name, **binding)
        assert track["created"] and track["valid"], track
        write("timeline_keyframe_set", trackId=track["trackId"], time=0, value=a)
        write("timeline_keyframe_set", trackId=track["trackId"], time=1, value=b)
        return track

    try:
        tools = client.request("tools/list", {})["tools"]
        names = {tool["name"] for tool in tools}
        assert {"character_control_set", "pose_bone_set", "pose_blendshape_set", "timeline_keyframe_set"} <= names
        deadline = time.monotonic() + 90
        while True:
            try:
                status = call("studio_status")
                if status["ready"] and status["poseEditor"]["available"] and status["timeline"]["available"]:
                    break
            except RuntimeError:
                if time.monotonic() >= deadline:
                    raise
            if time.monotonic() >= deadline:
                raise TimeoutError("Studio/KKPE/Timeline did not become ready.")
            time.sleep(2)
        assert status["writesEnabled"]
        session = status["sceneSession"]
        assert call("scene_snapshot")["total"] == 0, "Refuse to modify a nonempty Studio; start an owned disposable instance."
        baseline = write("scene_checkpoint")
        actor = write("add_character", path=str(fixture), sex=sex, name="StudioMCP animation integration")
        actor_id = actor["id"]
        for attempt in range(15):
            try:
                bones = call("pose_bones", {"objectId": actor_id, "query": "spine", "limit": 200})
                if bones.get("bones"):
                    break
            except RuntimeError:
                if attempt == 14:
                    raise
            time.sleep(0.5)
        bone = next(b for b in bones["bones"] if b["name"].lower() == "cf_s_spine01" and not b["overridden"])
        morphs = call("pose_blendshapes", {"objectId": actor_id, "limit": 200})["blendshapes"]
        morph = next(m for m in morphs if not m["overridden"])
        passed.append("Character import and KKPE bone/blendshape discovery")

        write("timeline_settings", duration=2, frameRate=30, speed=1)
        call("timeline_settings", {"sceneSession": session, "duration": 7, "frameRate": 0}, fail=True)
        close(call("timeline_get")["duration"], 2)
        assert call("timeline_get")["frameRate"] == 30

        write("character_kinematics_set", objectId=actor_id, mode="FK", enabled=True)
        state = write("character_kinematics_set", objectId=actor_id, mode="FK", group="Body", enabled=True)
        assert state["fkEnabled"] and not state["ikEnabled"]
        fk = next(c for c in call("character_controls", {"objectId": actor_id, "mode": "FK", "limit": 200})["controls"]
                  if "LeftArm" in c["groups"] and c["active"])
        call("character_control_set", {"sceneSession": session, "objectId": actor_id, "mode": "FK", "controlId": fk["controlId"],
                                      "rotation": [1, 2]}, fail=True)
        changed = write("character_control_set", objectId=actor_id, mode="FK", controlId=fk["controlId"], rotation=[10, 0, 0])
        close(changed["rotation"], [10, 0, 0])
        fk_track = keyed("fk_rotation", "MCP FK", [10, 0, 0], [30, 0, 0], controlId=fk["controlId"])
        duplicate = write("timeline_track_create", objectId=actor_id, kind="fk_rotation", controlId=fk["controlId"], name="Do not rename existing track")
        assert not duplicate["created"] and duplicate["trackId"] == fk_track["trackId"] and duplicate["alias"] == "MCP FK"
        call("timeline_keyframe_set", {"sceneSession": session, "trackId": fk_track["trackId"], "time": 1, "value": [90, 0, 0]}, fail=True)
        write("timeline_control", action="seek", time=0.5)
        fk_now = next(c for c in call("character_controls", {"objectId": actor_id, "mode": "FK", "limit": 200})["controls"] if c["controlId"] == fk["controlId"])
        close(fk_now["rotation"], [20, 0, 0])
        passed.append("Native FK writes, mode exclusion, native rotation track and midpoint interpolation")

        write("character_kinematics_set", objectId=actor_id, mode="IK", enabled=True)
        state = write("character_kinematics_set", objectId=actor_id, mode="IK", group="LeftArm", enabled=True)
        assert state["ikEnabled"] and not state["fkEnabled"]
        ik = next(c for c in call("character_controls", {"objectId": actor_id, "mode": "IK", "limit": 200})["controls"]
                  if c["group"] == "LeftArm" and c["active"] and c["canPosition"] and c["canRotation"])
        write("character_control_set", objectId=actor_id, mode="IK", controlId=ik["controlId"], position=ik["worldPosition"], space="world")
        a, b = ik["position"], [ik["position"][0] + 0.1, *ik["position"][1:]]
        ik_track = keyed("ik_position", "MCP IK", a, b, controlId=ik["controlId"])
        write("timeline_control", action="seek", time=0.5)
        ik_now = next(c for c in call("character_controls", {"objectId": actor_id, "mode": "IK", "limit": 200})["controls"] if c["controlId"] == ik["controlId"])
        close(ik_now["position"], [(x + y) / 2 for x, y in zip(a, b)])
        write("character_control_set", objectId=actor_id, mode="IK", controlId=ik["controlId"], rotation=[0, 0, 0], space="world")
        keyed("ik_rotation", "MCP IK rotation", [0, 0, 0], [0, 20, 0], controlId=ik["controlId"])
        write("timeline_control", action="seek", time=0.5)
        ik_now = next(c for c in call("character_controls", {"objectId": actor_id, "mode": "IK", "limit": 200})["controls"] if c["controlId"] == ik["controlId"])
        close(ik_now["rotation"], [0, 10, 0])
        passed.append("Native IK world target write and local-position track interpolation")

        bone_target = {"objectId": actor_id, "boneId": bone["boneId"]}
        position = [bone["position"][0] + 0.03, *bone["position"][1:]]
        scale = [s * 1.03 for s in bone["scale"]]
        edited = write("pose_bone_set", **bone_target, position=position, rotation=[10, 0, 0], scale=scale)
        close(edited["position"], position)
        close(edited["rotation"], [10, 0, 0])
        close(edited["scale"], scale)
        reset = write("pose_bone_reset", **bone_target)
        assert not reset["overridden"]
        close(reset["position"], edited["originals"]["position"])
        close(reset["scale"], edited["originals"]["scale"])
        bone_track = keyed("bone_position", "MCP bone", bone["position"], position, boneId=bone["boneId"])
        write("timeline_control", action="seek", time=0.5)
        bone_now = next(b for b in call("pose_bones", {"objectId": actor_id, "query": bone["name"], "limit": 200})["bones"] if b["boneId"] == bone["boneId"])
        close(bone_now["position"], [(x + y) / 2 for x, y in zip(bone["position"], position)])
        keyed("bone_rotation", "MCP bone rotation", [10, 0, 0], [30, 0, 0], boneId=bone["boneId"])
        keyed("bone_scale", "MCP bone scale", bone["scale"], [s * 1.1 for s in bone["scale"]], boneId=bone["boneId"])
        write("timeline_control", action="seek", time=0.5)
        bone_now = next(b for b in call("pose_bones", {"objectId": actor_id, "query": bone["name"], "limit": 200})["bones"] if b["boneId"] == bone["boneId"])
        close(bone_now["rotation"], [20, 0, 0])
        close(bone_now["scale"], [s * 1.05 for s in bone["scale"]])
        passed.append("KKPE position/rotation/scale writes, original reset and native bone-track interpolation")

        shape_target = {"objectId": actor_id, "rendererId": morph["rendererId"], "shapeName": morph["shapeName"]}
        original_weight = None
        for weight in (-15, 125):
            shape_edit = write("pose_blendshape_set", **shape_target, weight=weight)
            close(shape_edit["weight"], weight)
            if original_weight is None:
                original_weight = shape_edit["originalWeight"]
            close(shape_edit["originalWeight"], original_weight)
        reset = write("pose_blendshape_reset", **shape_target)
        assert not reset["overridden"]
        close(reset["weight"], original_weight)
        morph_track = keyed("blendshape", "MCP shape", 0, 50, rendererId=morph["rendererId"], shapeName=morph["shapeName"])
        write("timeline_control", action="seek", time=0.5)
        morph_now = next(m for m in call("pose_blendshapes", {"objectId": actor_id, "rendererId": morph["rendererId"], "query": morph["shapeName"]})["blendshapes"] if m["shapeName"] == morph["shapeName"])
        close(morph_now["weight"], 25)
        write("timeline_keyframe_set", trackId=morph_track["trackId"], time=0, value=0, curve="step", replace=True)
        write("timeline_control", action="seek", time=0.5)
        morph_now = next(m for m in call("pose_blendshapes", {"objectId": actor_id, "rendererId": morph["rendererId"], "query": morph["shapeName"]})["blendshapes"] if m["shapeName"] == morph["shapeName"])
        close(morph_now["weight"], 0)
        passed.append("KKPE negative/overdrive weights, original reset and native blendshape-track interpolation")

        root_track = keyed("object_position", "MCP root", [0, 0, 0], [0.1, 0, 0])
        write("timeline_keyframe_set", trackId=root_track["trackId"], time=1, value=[0.2, 0, 0], curve="step", replace=True)
        keys = call("timeline_keyframes", {"trackId": root_track["trackId"]})["keyframes"]
        assert len(keys) == 2 and keys[1]["curve"][0]["outTangent"] == "Infinity"
        write("timeline_keyframe_set", trackId=root_track["trackId"], time=1, value=[0.1, 0, 0], curve="linear", replace=True)
        write("timeline_keyframe_set", trackId=root_track["trackId"], time=1.5, value=[0.15, 0, 0])
        write("timeline_keyframe_remove", trackId=root_track["trackId"], time=1.5)
        assert call("timeline_keyframes", {"trackId": root_track["trackId"]})["total"] == 2
        write("timeline_track_set", trackId=root_track["trackId"], enabled=False, name="MCP root renamed")
        write("timeline_track_set", trackId=root_track["trackId"], enabled=True)
        write("timeline_control", action="play")
        call("timeline_keyframe_set", {"sceneSession": session, "trackId": root_track["trackId"], "time": 0}, fail=True)
        write("timeline_control", action="pause")
        write("timeline_control", action="seek", time=0.5)
        close(call("scene_snapshot")["objects"][0]["position"], [0.05, 0, 0])
        disposable = write("timeline_track_create", objectId=actor_id, kind="object_scale", name="MCP remove test")
        write("timeline_track_remove", trackId=disposable["trackId"])
        keyed("object_rotation", "MCP root rotation", [0, 0, 0], [0, 20, 0])
        keyed("object_scale", "MCP root scale", [1, 1, 1], [1.1, 1.1, 1.1])
        write("timeline_control", action="seek", time=0.5)
        root_state = call("scene_snapshot")["objects"][0]
        close(root_state["rotation"], [0, 10, 0])
        close(root_state["scale"], [1.05, 1.05, 1.05])
        passed.append("Native root track, duplicate protection, curves, keyframe replacement/deletion, rename, enable and playback")

        checkpoint = write("scene_checkpoint")
        write("scene_restore", path=checkpoint["path"])
        new_status = call("studio_status")
        assert new_status["sceneSession"] != session
        call("timeline_control", {"sceneSession": session, "action": "seek", "time": 0}, fail=True)
        session = new_status["sceneSession"]
        deadline = time.monotonic() + 15
        while not call("timeline_get")["available"]:
            if time.monotonic() >= deadline:
                raise TimeoutError("Timeline did not finish its native delayed scene restore.")
            time.sleep(0.25)
        write("timeline_control", action="pause")
        tracks = call("timeline_tracks")["tracks"]
        assert len(tracks) == 10 and all(t["valid"] and t["keyframeCount"] == 2 for t in tracks), tracks
        assert {t["owner"] for t in tracks} == {"Timeline", "KKPE"}
        close(call("timeline_get")["duration"], 2)
        assert call("timeline_get")["frameRate"] == 30
        write("timeline_control", action="seek", time=0.5)
        close(call("scene_snapshot")["objects"][0]["position"], [0.05, 0, 0])
        new_bone = next(b for b in call("pose_bones", {"objectId": actor_id, "query": bone["name"], "limit": 200})["bones"] if b["path"] == bone["path"])
        close(new_bone["position"], [(x + y) / 2 for x, y in zip(bone["position"], position)])
        close(new_bone["rotation"], [20, 0, 0])
        close(new_bone["scale"], [s * 1.05 for s in bone["scale"]])
        new_shape = next(m for m in call("pose_blendshapes", {"objectId": actor_id, "query": morph["shapeName"], "limit": 200})["blendshapes"] if m["shapeName"] == morph["shapeName"] and m["renderer"] == morph["renderer"])
        close(new_shape["weight"], 0)
        saved_morph = next(t for t in tracks if t["alias"] == "MCP shape")
        assert call("timeline_keyframes", {"trackId": saved_morph["trackId"]})["keyframes"][0]["curve"][0]["outTangent"] == "Infinity"
        passed.append("Scene checkpoint/reload restores native Timeline and KKPE data; fresh session/handles and midpoint values verified")

        assert call("timeline_tracks")["total"] == 10
        write("camera_set", pivot=[0, 0.9, 0], rotation=[0, 180, 0], distance=[0, 0, -2.5], fov=35)
        capture = write("capture", width=480, height=640)
        assert capture["state"] == "complete"
        assert hashlib.sha256(card.read_bytes()).hexdigest() == original_hash
        summary = {"tools": len(tools), "trackKinds": 10, "savedTracks": 10, "savedKeyframes": 20,
                   "passed": passed, "checkpoint": checkpoint["path"], "baseline": baseline["path"], "sourceCardUnchanged": True}
        (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"tools": len(tools), "passed": passed, "sourceCardUnchanged": True}, indent=2))
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--card", required=True)
    parser.add_argument("--sex", choices=("female", "male"), default="female")
    parser.add_argument("--server", default="src/StudioMcp.Host/bin/Release/net8.0/StudioMcp.Host.dll")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    run(args.game_root, args.card, args.sex, args.server, args.output)
