#!/usr/bin/env python3
"""Parses the PowerShell embedded in .github/workflows/*.yml `run:` blocks.

The PowerShell lint job already parses every .ps1 in the tree, but a `run:`
block written in pwsh is never looked at by anything. That gap shipped: the
IndexNow workflow carried `$maxAttempts:` inside a double-quoted string, which
PowerShell reads as a scope qualifier, so the whole step was a ParserError and
the submission had never once run. Nothing was red, because nothing was
looking. This script closes that: same blast radius as a broken
package-release.ps1, same gate.

Detection is by declared shell only - `shell: pwsh` / `shell: powershell` on
the step, or inherited from the job's or the workflow's `defaults.run.shell`.
A step with no shell and no PowerShell default runs under bash and is skipped;
content is never used to guess, because guessing is how a checker starts
reporting on scripts it was never asked about.

PowerShell syntax is not reimplemented here. Every block is handed to the real
parser ([System.Management.Automation.Language.Parser]::ParseFile) through one
pwsh invocation, exactly as .github/workflows/ps-lint.yml does for .ps1 files.

Every error is reported against the real line in the .yml file, not the line
inside the extracted block, so the GitHub annotation
(::error file=<path>,line=<n>,title=<t>::<message>) lands on the offending
line in the PR diff.

Usage:
    python3 scripts/check-workflow-pwsh.py
    python3 scripts/check-workflow-pwsh.py --repo-root /path/to/magic-tray

Requires PyYAML and pwsh. Exits non-zero if any block fails to parse.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
from typing import NamedTuple

try:
    import yaml
except ModuleNotFoundError:
    sys.exit("check-workflow-pwsh.py needs PyYAML. Install it with:\n"
             "    python3 -m pip install 'PyYAML==6.0.3'\n"
             "(the same pinned version check-winget-manifest.py runs against).")

WORKFLOW_DIR = ".github/workflows"
ANNOTATION_TITLE = "Workflow PowerShell parse error"
# GitHub's shell keywords for PowerShell. The value can carry arguments
# (`pwsh -command ". '{0}'"`), so only the first token is matched.
POWERSHELL_SHELLS = ("pwsh", "powershell")

# ${{ }} is a GitHub expression: Actions interpolates it before the shell is
# ever started, so what PowerShell sees is the *result*, not this text. Handing
# the text to the parser unchanged produces errors that do not exist - pwsh
# reads `${{ github.sha }}` as a braced variable name and reports "Use `{
# instead of { in variable names" plus a stray '}' (verified against pwsh
# 7.4.6). A checker that invents errors gets switched off, which is the failure
# mode .ai/validators/pr-admission-check.py documents at length; so each
# expression is replaced with a benign bareword before parsing.
#
# The placeholder is a run of 'x' the same length as the expression, with any
# newlines kept. Same length and same line count means reported line and column
# numbers still point at the real source position, and a bareword parses in
# every position an interpolated value can occupy: inside '...' and "..."
# (where quotes would have terminated the string early), as a bare argument, or
# as a whole command. It is deliberately not type-correct - the parser checks
# syntax, not whether an argument is a number.
GHA_EXPRESSION = re.compile(r"\$\{\{.*?\}\}", re.DOTALL)

# Parses each extracted block with the real parser and reports positions
# relative to the block; Python maps those back to .yml lines.
PWSH_RUNNER = r"""
param([Parameter(Mandatory = $true)][string]$Manifest)
$ErrorActionPreference = 'Stop'
$blocks = @(Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json)
$results = foreach ($block in $blocks) {
  $tokens = $null
  $parseErrors = $null
  [void][System.Management.Automation.Language.Parser]::ParseFile(
    $block.path, [ref]$tokens, [ref]$parseErrors)
  $found = @()
  if ($parseErrors) {
    foreach ($parseError in $parseErrors) {
      $found += [ordered]@{
        line    = $parseError.Extent.StartLineNumber
        column  = $parseError.Extent.StartColumnNumber
        message = $parseError.Message
      }
    }
  }
  [ordered]@{ id = $block.id; errors = $found }
}
ConvertTo-Json -InputObject @($results) -Depth 6 -Compress
"""


class Block(NamedTuple):
    """One PowerShell `run:` block, with its lines mapped to the .yml file."""

    path: pathlib.Path
    job: str
    step_index: int          # 1-based position in the job's steps list
    step_name: str
    shell: str
    script: str              # verbatim, as written in the workflow
    lines: tuple[int, ...]   # lines[i] = .yml line of script line i
    anchor: int              # .yml line of the `run:` key itself
    mapped: bool             # False when the 1:1 line mapping does not hold


def escape(message: str) -> str:
    """GitHub annotation escaping, as the .ps1 parse step does it."""
    return message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def child(node: yaml.Node, key: str) -> yaml.Node | None:
    """Value node for `key`, or None. Nodes, not dicts: only the node tree
    carries the line numbers this script exists to report."""
    if not isinstance(node, yaml.MappingNode):
        return None
    for name, value in node.value:
        if isinstance(name, yaml.ScalarNode) and name.value == key:
            return value
    return None


def text(node: yaml.Node | None) -> str | None:
    return node.value if isinstance(node, yaml.ScalarNode) else None


def defaults_shell(node: yaml.Node | None) -> str | None:
    """`defaults.run.shell` of a workflow or a job."""
    return text(child(child(child(node, "defaults"), "run"), "shell"))


def is_powershell(shell: str | None) -> bool:
    return bool(shell) and shell.split()[0].lower() in POWERSHELL_SHELLS


def map_lines(node: yaml.ScalarNode, script_lines: list[str],
              file_lines: list[str]) -> tuple[tuple[int, ...], int, bool]:
    """Maps each line of a `run:` block back to its line in the .yml file.

    A literal block scalar (`run: |`) keeps line structure verbatim, so line i
    of the value sits on the line after the `run: |` header plus i. That is
    asserted rather than assumed: every line is compared (stripped of the
    block indentation) against the file line it claims to be. Any other scalar
    style - folded `>`, quoted, plain - does not keep a 1:1 relationship, so
    those anchor on the `run:` key instead of reporting a line that is wrong.
    """
    anchor = node.start_mark.line + 1
    if node.style != "|":
        return (anchor,) * len(script_lines), anchor, len(script_lines) <= 1
    first = anchor + 1
    mapped = tuple(first + offset for offset in range(len(script_lines)))
    aligned = all(
        line <= len(file_lines) and file_lines[line - 1].strip() == script.strip()
        for line, script in zip(mapped, script_lines)
    )
    if not aligned:
        return (anchor,) * len(script_lines), anchor, False
    return mapped, anchor, True


def collect(path: pathlib.Path, problems: list[str]) -> list[Block]:
    """Every PowerShell `run:` block in one workflow file."""
    source = path.read_text(encoding="utf-8")
    file_lines = source.splitlines()
    try:
        root = yaml.compose(source)
    except yaml.YAMLError as error:
        message = "Not valid YAML: %s" % str(error).replace("\n", " ")
        problems.append(message)
        print("::error file=%s,line=1,title=%s::%s"
              % (path.as_posix(), ANNOTATION_TITLE, escape(message)))
        return []

    workflow_shell = defaults_shell(root)
    jobs = child(root, "jobs")
    blocks: list[Block] = []
    if not isinstance(jobs, yaml.MappingNode):
        return blocks

    for job_key, job in jobs.value:
        job_name = text(job_key) or "?"
        job_shell = defaults_shell(job) or workflow_shell
        steps = child(job, "steps")
        if not isinstance(steps, yaml.SequenceNode):
            continue  # a `uses:` job (reusable workflow) has no steps
        for index, step in enumerate(steps.value, start=1):
            run = child(step, "run")
            if not isinstance(run, yaml.ScalarNode):
                continue
            shell = text(child(step, "shell")) or job_shell
            if not is_powershell(shell):
                continue
            script_lines = run.value.splitlines()
            lines, anchor, mapped = map_lines(run, script_lines, file_lines)
            blocks.append(Block(
                path=path,
                job=job_name,
                step_index=index,
                step_name=text(child(step, "name")) or "step %d" % index,
                shell=shell,
                script=run.value,
                lines=lines,
                anchor=anchor,
                mapped=mapped,
            ))
    return blocks


def mask_expressions(script: str) -> str:
    """Replaces every ${{ }} with an equal-length, equal-line-count bareword."""
    return GHA_EXPRESSION.sub(
        lambda match: "".join("\n" if char == "\n" else "x" for char in match.group(0)),
        script,
    )


def parse_with_pwsh(blocks: list[Block]) -> list[list[dict]]:
    """One pwsh invocation for the whole set; errors per block, in order."""
    with tempfile.TemporaryDirectory(prefix="workflow-pwsh-") as folder:
        work = pathlib.Path(folder)
        manifest = []
        for index, block in enumerate(blocks):
            script = work / ("block-%03d.ps1" % index)
            script.write_text(mask_expressions(block.script), encoding="utf-8")
            manifest.append({"id": index, "path": str(script)})
        (work / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        runner = work / "parse-blocks.ps1"
        runner.write_text(PWSH_RUNNER, encoding="utf-8")
        try:
            completed = subprocess.run(
                ["pwsh", "-NoProfile", "-NoLogo", "-NonInteractive",
                 "-File", str(runner), "-Manifest", str(work / "manifest.json")],
                capture_output=True, text=True, check=False,
            )
        except FileNotFoundError:
            sys.exit("check-workflow-pwsh.py needs pwsh on PATH: the PowerShell "
                     "parser is the only authority on PowerShell syntax.\n"
                     "Install PowerShell 7 (the ubuntu-latest runner image ships it).")

    if completed.returncode != 0 or not completed.stdout.strip():
        sys.exit("pwsh failed to parse the extracted blocks (exit %d):\n%s%s"
                 % (completed.returncode, completed.stdout, completed.stderr))
    results = {entry["id"]: entry.get("errors") or []
               for entry in json.loads(completed.stdout)}
    return [results.get(index, []) for index in range(len(blocks))]


def report(block: Block, errors: list[dict]) -> int:
    """Annotates one block's errors on their real .yml lines. Returns the count."""
    step = "[%s] step %d %r" % (block.job, block.step_index, block.step_name)
    if not errors:
        print("ok   %s:%d %s" % (block.path.as_posix(), block.anchor, step))
        return 0
    for error in errors:
        offset = int(error.get("line") or 1) - 1
        if 0 <= offset < len(block.lines):
            line = block.lines[offset]
        else:
            line = block.lines[-1] if block.lines else block.anchor
        # Keep the in-block position when it could not be mapped, so the error
        # is still findable even though the annotation sits on the `run:` key.
        detail = "" if block.mapped else " (line %d of the run: block)" % (offset + 1)
        message = "%s [%s] step %d %r (shell: %s): %s%s" % (
            block.path.as_posix(), block.job, block.step_index, block.step_name,
            block.shell, error.get("message") or "parse error", detail)
        print("::error file=%s,line=%d,title=%s::%s"
              % (block.path.as_posix(), line, ANNOTATION_TITLE, escape(message)))
    return len(errors)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--repo-root",
        type=pathlib.Path,
        default=pathlib.Path(__file__).resolve().parent.parent,
        help="repository root to check; defaults to the parent of this script's folder",
    )
    # Every path printed from here on is relative to the repository root, which
    # is what a GitHub annotation needs to attach itself to the diff.
    os.chdir(parser.parse_args().repo_root)
    root = pathlib.Path(WORKFLOW_DIR)

    workflows = sorted(p for p in root.glob("*.y*ml") if p.suffix in (".yml", ".yaml"))
    if not workflows:
        print("::error file=%s,line=1,title=%s::No workflow file found - the path "
              "filter or checkout is wrong." % (root.as_posix(), ANNOTATION_TITLE))
        return 1

    problems: list[str] = []
    blocks: list[Block] = []
    for workflow in workflows:
        blocks.extend(collect(workflow, problems))

    if not blocks:
        print("::error file=%s,line=1,title=%s::No PowerShell run: block found - "
              "the shell detection or the checkout is wrong."
              % (root.as_posix(), ANNOTATION_TITLE))
        return 1

    errors = sum(report(block, found)
                 for block, found in zip(blocks, parse_with_pwsh(blocks)))
    errors += len(problems)

    if errors:
        print("Workflow pwsh parse check: %d syntax error(s) across %d block(s)."
              % (errors, len(blocks)))
        return 1
    print("Workflow pwsh parse check: %d block(s) parsed cleanly." % len(blocks))
    return 0


if __name__ == "__main__":
    sys.exit(main())
