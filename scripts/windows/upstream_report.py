#!/usr/bin/env python3
"""Report incoming cmux main changes without merging or advancing the baseline."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess
import sys


REPOSITORY = "https://github.com/manaflow-ai/cmux.git"
DEFAULT_BASELINE = Path(__file__).resolve().parents[2] / "docs/windows-upstream-baseline.json"


class ReportError(Exception):
    pass


def git(repo: Path, *arguments: str) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(repo), *arguments], stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, timeout=60, check=False,
    )
    if result.returncode:
        raise ReportError(result.stderr.decode("utf-8", errors="replace").strip() or f"git exited {result.returncode}")
    return result.stdout


def resolve(repo: Path, ref: str) -> str:
    return git(repo, "rev-parse", "--verify", "--end-of-options", ref + "^{commit}").decode("ascii").strip()


def load_baseline(path: Path) -> dict:
    try:
        with path.open("rb") as source:
            data = source.read(16385)
        if len(data) > 16384:
            raise ReportError("Baseline exceeds 16 KiB.")
        value = json.loads(data)
    except (OSError, ValueError) as error:
        raise ReportError(f"Cannot read baseline: {error}") from error
    if not isinstance(value, dict) or value.get("repository") != REPOSITORY or value.get("branch") != "main":
        raise ReportError("Baseline must identify manaflow-ai/cmux main.")
    if not isinstance(value.get("commit"), str) or not re.fullmatch(r"[0-9a-f]{40}", value["commit"]):
        raise ReportError("Baseline commit must be a full 40-character lowercase SHA.")
    return value


def feature_hints(path: str) -> list[str]:
    """Broad review hints only; neither a complete inventory nor a parity verdict."""
    lowered = path.lower()
    hints = {"F-16"}
    for tokens, groups in (
        (("workspace", "split", "tabmanager", "bonsplit", "dock"), ("F-01", "F-10")),
        (("terminal", "ghostty", "pty"), ("F-02", "F-11")),
        (("sidebar", "gitbranch", "pullrequest", "portscan"), ("F-03",)),
        (("notification", "notify"), ("F-04", "F-09")),
        (("cli/", "controlsocket", "socket", "protocol", "sdk"), ("F-05",)),
        (("browser", "webview", "playwright"), ("F-06", "F-07")),
        (("ssh", "remote", "tunnel"), ("F-08",)),
        (("agent", "codex", "claude", "hook"), ("F-09",)),
        (("persist", "restore", "session"), ("F-10",)),
        (("settings", "config", "shortcut", "keybind", "theme", "font"), ("F-11",)),
        (("web/", "cloud", "auth", "iroh", "relay", "presence"), ("F-12", "F-13")),
        (("ios/", "packages/ios/", "iphone", "mobile", "pairing"), ("F-13",)),
        (("winget", "installer", "release", "updat", "appcast", "sparkle"), ("F-14", "F-15")),
    ):
        if any(token in lowered for token in tokens):
            hints.update(groups)
    return sorted(hints)


def build_report(repo: Path, baseline: dict, target_ref: str) -> dict:
    base = resolve(repo, baseline["commit"])
    target = resolve(repo, target_ref)
    # A rewritten or unrelated history needs an explicit review. Never turn a
    # two-dot diff against an unrelated commit into an apparently clean report.
    ancestor = subprocess.run(
        ["git", "-C", str(repo), "merge-base", "--is-ancestor", base, target],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60, check=False,
    )
    if ancestor.returncode:
        raise ReportError("Baseline is not an ancestor of target, or ancestry could not be checked; review history before continuing.")
    raw_paths = git(repo, "diff", "--name-status", "--no-renames", "-z", base, target, "--").split(b"\0")
    changes = []
    for index in range(0, len(raw_paths) - 1, 2):
        status = raw_paths[index].decode("ascii")
        path = raw_paths[index + 1].decode("utf-8", errors="surrogateescape")
        changes.append({"status": status, "path": path, "feature_hints": feature_hints(path), "review": "required"})
    # Read one record per commit with NUL-delimited subjects, preserving Unicode
    # and avoiding Git's path quoting conventions on Windows.
    history = git(repo, "log", "--reverse", "--format=%H%x00%s", "-z", f"{base}..{target}", "--").split(b"\0")
    commits = []
    for index in range(0, len(history) - 1, 2):
        commits.append({"commit": history[index].decode("ascii"), "subject": history[index + 1].decode("utf-8", errors="replace")})
    return {
        "repository": REPOSITORY, "branch": "main",
        "observed_at": datetime.now(timezone.utc).isoformat(),
        "target_ref": target_ref, "freshness": "local-ref; fetch upstream main separately before reporting",
        "baseline_commit": base, "target_commit": target,
        "windows_commit": resolve(repo, "HEAD"),
        "windows_worktree_dirty": bool(git(repo, "status", "--porcelain", "-z", "--untracked-files=normal")),
        "incoming_commit_count": len(commits), "commits": commits, "changes": changes,
        "feature_inventory_review_required": bool(commits),
        "parity_verdict": "not-evaluated",
    }


def markdown(report: dict) -> str:
    lines = [
        "# cmux Windows upstream report", "",
        f"- Repository: {report['repository']} (main)",
        f"- Observed: {report['observed_at']}",
        f"- Baseline: `{report['baseline_commit']}`",
        f"- Target: `{report['target_commit']}`",
        "- Freshness: local ref only; fetch upstream main separately before reporting.",
        f"- Windows: `{report['windows_commit']}` (dirty: {str(report['windows_worktree_dirty']).lower()})",
        f"- Incoming commits: {report['incoming_commit_count']}",
        "- Feature parity: not evaluated. Review every incoming commit and update the inventory/tests.", "",
    ]
    for commit in report["commits"]:
        # JSON quoting keeps control characters and Markdown in subjects/paths
        # from becoming active content when pasted into a progress report.
        lines.append(f"- `{commit['commit']}`: {json.dumps(commit['subject'], ensure_ascii=True)}")
    lines += ["", "```json", json.dumps(report["changes"], indent=2, ensure_ascii=True), "```", ""]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--target", default="refs/remotes/upstream/main", help="Freshly fetched local upstream main ref")
    parser.add_argument("--format", choices=("json", "markdown"), default="json")
    parser.add_argument("--output", type=Path, help="Write a report; the baseline is never changed")
    args = parser.parse_args(argv)
    try:
        if args.output and (args.output.resolve() == args.baseline.resolve()
                            or (args.output.exists() and args.output.samefile(args.baseline))):
            raise ReportError("Report output cannot overwrite the baseline.")
        report = build_report(args.repo, load_baseline(args.baseline), args.target)
        rendered = markdown(report) if args.format == "markdown" else json.dumps(report, indent=2, ensure_ascii=True) + "\n"
        if args.output:
            args.output.write_text(rendered, encoding="utf-8")
        else:
            print(rendered, end="")
        return 0
    except (ReportError, OSError, subprocess.TimeoutExpired) as error:
        print(f"upstream-report: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
