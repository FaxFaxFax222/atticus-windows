"""Draw on screen: the model boundary, picture-pixel mapping, the helper-to-box hand-off and the safety gate."""
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import atticus
import drawing

MONITORS = [{'name': 'Built-in', 'x': 0, 'y': 0, 'w': 2560, 'h': 1600, 'built_in': True},
            {'name': 'DELL', 'x': 2560, 'y': 0, 'w': 1280, 'h': 1024, 'built_in': False}]


class DrawingTests(unittest.TestCase):
    def test_picture_pixels_map_to_screen_pixels(self):
        maps = drawing.mappings(MONITORS)
        self.assertEqual(maps['1'], {'frame': [0, 0, 2560, 1600], 'pixels': [1280, 800]})
        self.assertEqual(maps['2']['pixels'], [1280, 1024])   # already 1280 wide: not resized
        out = drawing.drawing({'steps': [{'say': 'Click Save', 'screen': 1, 'hit': [100, 50, 20, 10], 'wait': True,
                                          'shapes': [{'kind': 'rect', 'rect': [100, 50, 20, 10]}]}]}, maps)
        self.assertEqual(out['steps'][0]['shapes'][0]['rect'], [200, 100, 40, 20])
        self.assertEqual(out['steps'][0]['hit'], [200, 100, 40, 20])
        self.assertNotIn('screen', out['steps'][0])
        self.assertFalse(out['teaching'])
        on_second = drawing.drawing({'steps': [{'say': 'x', 'screen': 2, 'shapes': [{'kind': 'pointer', 'at': [10, 20]}]}]}, maps)
        self.assertEqual(on_second['steps'][0]['shapes'][0]['at'], [2570, 20])

    def test_teaching_needs_captions_and_waiting_needs_a_hit(self):
        board = {'steps': [{'say': 'a² + b² = c²', 'shapes': [{'kind': 'text', 'text': '3² + 4² = 5²'}]}]}
        self.assertTrue(drawing.drawing(board)['teaching'])
        for bad in [{'steps': [{'shapes': [{'kind': 'text', 'text': 'x'}]}]},                                  # no caption
                    {'steps': [{'say': 'x', 'wait': True, 'shapes': [{'kind': 'pointer', 'at': [1, 1]}]}]},    # wait without hit
                    {'steps': [{'say': 'x', 'target': 'Save', 'shapes': [{'kind': 'rect'}]}]},                 # Mac's AX targets
                    {'steps': [{'say': 'x', 'shapes': [{'kind': 'rect', 'rect': [0, 0, -1, 5]}]}]},
                    {'steps': [{'say': 'x', 'shapes': [{'kind': 'arrow', 'points': [[0, 0]]}]}]},
                    {'steps': [{'say': 'x', 'shapes': [{'kind': 'label', 'at': [0, 0], 'text': 'x', 'color': 'pink'}]}]},
                    {'steps': [{'say': 'x', 'screen': 1, 'shapes': [{'kind': 'pointer', 'at': [5000, 0]}]}]},  # off the picture
                    {'steps': []}, {'steps': [{}] * 13}]:
            with self.assertRaises(ValueError):
                drawing.drawing(bad, drawing.mappings(MONITORS))

    def test_context_tells_the_follow_up_what_is_on_screen(self):
        self.assertEqual(drawing.context(None), '')
        text = drawing.context({'steps': [{'say': 'Click File'}], 'shown': 1})
        self.assertIn('step 1 of 1', text)
        self.assertIn('Click File', text)

    def test_captions_never_ask_but_a_chained_delete_still_does(self):
        with patch.object(atticus, 'typing_state', return_value={}):
            guide = atticus.DRAW + ' \'{"steps":[{"say":"Click Send; then delete the draft","shapes":[{"kind":"pointer","at":[1,1]}]}]}\''
            self.assertEqual(atticus.risky(guide), '')
            self.assertEqual(atticus.risky(guide + '; rm -rf ~/x'), 'Delete something?')
        self.assertTrue(atticus.UNSAFE.search(guide))   # never learned as a no-AI shortcut

    def test_a_helper_drawing_reaches_the_box_and_waits_for_its_receipt(self):
        ask = tempfile.mkdtemp()
        with open(os.path.join(ask, 'draw-screens.json'), 'w') as f:
            json.dump({'screens': drawing.mappings(MONITORS)}, f)
        env = {'ATTICUS_ASK': ask, 'ATTICUS_UI': '1'}
        def app():   # Atticus.exe: reads the line relay() would print, puts the marks up, answers
            for _ in range(100):
                names = [n for n in os.listdir(ask) if n.startswith('emit-')]
                if names:
                    with open(os.path.join(ask, names[0]), encoding='utf-8') as f: line = f.read()
                    self.assertTrue(line.startswith('✎ '))
                    with open(json.loads(line[2:])['receipt'], 'w') as f: f.write('shown')
                    return
                threading.Event().wait(0.05)
        t = threading.Thread(target=app); t.start()
        with patch.dict(os.environ, env), patch.object(atticus, 'pref', side_effect=lambda k, d: d):
            result = atticus.draw({'steps': [{'say': 'Here', 'screen': 1, 'shapes': [{'kind': 'circle', 'rect': [10, 10, 20, 20]}]}]})
        t.join()
        self.assertEqual(result, 'Guide shown (step 1 of 1). Leave it running.')
        self.assertEqual([n for n in os.listdir(ask) if n.startswith('draw-result-')], [])   # the receipt is gone

    def test_drawing_without_this_asks_look_refuses(self):
        with patch.dict(os.environ, {'ATTICUS_ASK': tempfile.mkdtemp(), 'ATTICUS_UI': '1'}), patch.object(atticus, 'pref', side_effect=lambda k, d: d):
            with self.assertRaisesRegex(ValueError, 'Read its pictures'):
                atticus.draw({'steps': [{'say': 'x', 'shapes': [{'kind': 'pointer', 'at': [1, 1]}]}]})

    def test_prompt_with_drawing_stays_under_budget(self):
        full = (atticus.SYSTEM.replace('NEVER_RISKY', atticus.ASK_FIRST) + atticus.ASK_ME + drawing.DRAWING + atticus.LEARN_ME +
                atticus.TEACH + atticus.WORKFLOW + atticus.JOPLIN_TOOLS)
        self.assertLess(len(full), 10000)


if __name__ == '__main__':
    unittest.main()
