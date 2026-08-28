#!/usr/bin/env python3
import argparse
import json
import ssl
import subprocess
import sys
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse


def ensure_certificate(cert_path: Path, key_path: Path) -> None:
    if cert_path.exists() and key_path.exists():
        return

    cert_path.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        "openssl",
        "req",
        "-x509",
        "-newkey",
        "rsa:2048",
        "-nodes",
        "-keyout",
        str(key_path),
        "-out",
        str(cert_path),
        "-days",
        "3650",
        "-subj",
        "/CN=localhost",
    ]

    try:
        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except FileNotFoundError:
        sys.exit("openssl is required to generate a local HTTPS certificate")
    except subprocess.CalledProcessError as exc:
        sys.exit(f"failed to generate local HTTPS certificate: {exc}")


def make_handler(output_dir: Path):
    class SinkHandler(BaseHTTPRequestHandler):
        server_version = "CyberstatDummySink/0.1"

        def do_POST(self):
            parsed_url = urlparse(self.path)
            query = parse_qs(parsed_url.query)
            payload_type = query.get("type", ["unknown"])[0] or "unknown"
            program = query.get("program", ["unknown"])[0] or "unknown"

            body_length = int(self.headers.get("Content-Length", "0"))
            raw_body = self.rfile.read(body_length)
            received_at = datetime.now(timezone.utc)

            try:
                body = json.loads(raw_body.decode("utf-8"))
                formatted_body = json.dumps(body, indent=2, sort_keys=True)
            except Exception:
                body = None
                formatted_body = raw_body.decode("utf-8", errors="replace")

            safe_type = "".join(ch if ch.isalnum() or ch in ("-", "_") else "_" for ch in payload_type)
            timestamp = received_at.strftime("%Y%m%dT%H%M%S.%fZ")
            filename = output_dir / f"{timestamp}_{safe_type}.json"

            record = {
                "received_at": received_at.isoformat(),
                "method": self.command,
                "path": parsed_url.path,
                "query": query,
                "program": program,
                "type": payload_type,
                "headers": dict(self.headers),
                "body": body,
            }

            if body is None:
                record["raw_body"] = formatted_body

            output_dir.mkdir(parents=True, exist_ok=True)
            filename.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8")

            self.send_response(204)
            self.end_headers()
            print(f"{received_at.isoformat()} wrote {filename}", flush=True)

        def log_message(self, fmt, *args):
            print(f"{self.address_string()} - {fmt % args}", flush=True)

    return SinkHandler


def main() -> int:
    parser = argparse.ArgumentParser(description="Local HTTPS sink for Cyberstat telemetry payloads.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", default=2145, type=int)
    parser.add_argument("--output-dir", default="sink-payloads")
    parser.add_argument("--cert", default=".local/dummy-sink/cert.pem")
    parser.add_argument("--key", default=".local/dummy-sink/key.pem")
    args = parser.parse_args()

    output_dir = Path(args.output_dir).resolve()
    cert_path = Path(args.cert).resolve()
    key_path = Path(args.key).resolve()

    ensure_certificate(cert_path, key_path)

    server = ThreadingHTTPServer((args.host, args.port), make_handler(output_dir))
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certfile=cert_path, keyfile=key_path)
    server.socket = context.wrap_socket(server.socket, server_side=True)

    print(f"Cyberstat dummy sink listening at https://{args.host}:{args.port}/", flush=True)
    print(f"Writing payloads to {output_dir}", flush=True)
    server.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
