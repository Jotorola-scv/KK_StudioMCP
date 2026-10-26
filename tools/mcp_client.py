"""Exercise a stdio MCP server without any desktop/UI automation."""
import argparse
import base64
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time


class McpClient:
    def __init__(self, server, output):
        self.output = Path(output)
        self.output.mkdir(parents=True, exist_ok=True)
        self.log = (self.output / "server-stderr.log").open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            ["dotnet", str(Path(server).resolve())],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log,
            encoding="utf-8", env=os.environ.copy(), cwd=self.output,
        )
        self.messages = queue.Queue()
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()
        self.server_info = self.request("initialize", {
            "protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "studio-mcp-verifier", "version": "0.1.0"},
        })
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _read(self):
        for line in self.process.stdout:
            try:
                self.messages.put(json.loads(line))
            except json.JSONDecodeError:
                pass

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def request(self, method, params, timeout=60):
        self.next_id += 1
        request_id = self.next_id
        self.send({"jsonrpc": "2.0", "id": request_id,
                   "method": method, "params": params})
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                message = self.messages.get(timeout=min(1, deadline-time.monotonic()))
            except queue.Empty:
                if self.process.poll() is not None:
                    raise RuntimeError("MCP server exited; inspect server-stderr.log")
                continue
            if message.get("id") != request_id:
                continue
            if "error" in message:
                raise RuntimeError(message["error"])
            return message["result"]
        raise TimeoutError(f"No MCP response for {method}")

    def close(self):
        if self.process.stdin:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=3)
        self.log.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--call")
    parser.add_argument("--param", action="append", default=[])
    args = parser.parse_args()
    arguments = {}
    for entry in args.param:
        key, value = entry.split("=", 1)
        try:
            arguments[key] = json.loads(value)
        except json.JSONDecodeError:
            arguments[key] = value
    client = McpClient(args.server, args.output)
    try:
        result = client.request("tools/call", {"name": args.call, "arguments": arguments}) \
            if args.call else client.request("tools/list", {})
        (client.output / "response.json").write_text(
            json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        if not args.call:
            for tool in result["tools"]:
                print(tool["name"], list(tool.get("inputSchema", {}).get("properties", {})))
        else:
            for index, content in enumerate(result.get("content", [])):
                if content.get("type") == "image":
                    path = client.output / f"image-{index}.png"
                    path.write_bytes(base64.b64decode(content["data"]))
                    print("IMAGE", path.resolve())
                elif content.get("type") == "text":
                    print(content.get("text", "")[:5000])
                    if content.get("text", "").startswith("❌"):
                        raise RuntimeError("Legacy MCP tool reported failure in text content")
            if result.get("isError"):
                raise RuntimeError("MCP tool reported an error")
    finally:
        client.close()


if __name__ == "__main__":
    main()
