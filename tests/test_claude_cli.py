"""Atticus must run Claude Code, never the Claude desktop app, which also answers to `claude` on Windows."""
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import atticus


def tool(folder, name='claude.exe' if sys.platform == 'win32' else 'claude'):
    os.makedirs(folder, exist_ok=True)
    p = os.path.join(folder, name)
    with open(p, 'w') as f: f.write('#!/bin/sh\n')
    os.chmod(p, os.stat(p).st_mode | stat.S_IEXEC)
    return p


class ClaudeCliTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        patch.object(atticus, 'HOME', os.path.join(self.root, 'home')).start()
        self.addCleanup(patch.stopall)

    def path(self, *dirs):
        return patch.dict(os.environ, {'PATH': os.pathsep.join(dirs), 'APPDATA': os.path.join(self.root, 'appdata')})

    def test_the_desktop_apps_alias_is_skipped(self):
        alias = os.path.join(self.root, 'AppData', 'Local', 'Microsoft', 'WindowsApps')
        app = os.path.join(self.root, 'AppData', 'Local', 'AnthropicClaude')
        winget = os.path.join(self.root, 'AppData', 'Local', 'Microsoft', 'WinGet', 'Links')
        tool(alias); tool(app); cli = tool(winget)
        with self.path(alias, app, winget):
            self.assertEqual(atticus.claude_cli(), cli)
        with self.path(alias, app):
            self.assertIsNone(atticus.claude_cli())
            self.assertEqual(atticus.plan()['need'], 'install')   # setup offers to install Claude Code instead of opening the app

    def test_the_native_install_wins_over_path(self):
        native = tool(os.path.join(self.root, 'home', '.local', 'bin'), 'claude.exe')
        other = os.path.join(self.root, 'other'); tool(other)
        with self.path(other):
            self.assertEqual(atticus.claude_cli(), native)

    def test_desktop_folders_are_recognized_on_both_separators(self):
        for d in [r'C:\Users\a\AppData\Local\Microsoft\WindowsApps', r'C:\Users\a\AppData\Local\AnthropicClaude\app-1.0',
                  r'C:\Users\a\AppData\Local\Programs\Claude', 'C:/Program Files/WindowsApps/Claude_1.0_x64/app']:
            self.assertTrue(atticus.DESKTOP_APP.search(d), d)
        for d in [r'C:\Users\a\.local\bin', r'C:\Users\a\AppData\Roaming\npm', r'C:\Users\a\AppData\Local\Programs\Claude Code']:
            self.assertFalse(atticus.DESKTOP_APP.search(d), d)


if __name__ == '__main__':
    unittest.main()
