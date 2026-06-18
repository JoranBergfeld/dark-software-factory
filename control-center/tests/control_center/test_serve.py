"""Control Center ``main()`` serve entrypoint (``dsf-control-center`` script)."""

from __future__ import annotations

from dsf.control_center.app import main


def test_main_launches_uvicorn_with_defaults(monkeypatch):
    calls: list[tuple[str, dict]] = []
    monkeypatch.setattr("uvicorn.run", lambda target, **kw: calls.append((target, kw)))

    assert main([]) == 0

    assert calls == [("dsf.control_center.app:app", {"host": "127.0.0.1", "port": 8081})]


def test_main_honours_host_and_port(monkeypatch):
    calls: list[tuple[str, dict]] = []
    monkeypatch.setattr("uvicorn.run", lambda target, **kw: calls.append((target, kw)))

    assert main(["--host", "0.0.0.0", "--port", "9000"]) == 0

    assert calls == [("dsf.control_center.app:app", {"host": "0.0.0.0", "port": 9000})]
