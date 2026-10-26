"""Draw on screen: the AI's guide or whiteboard explanation, checked here, drawn by Atticus.exe over the user's screens.
Ported from atticus-mac. Coordinates are Windows virtual-screen pixels (top-left of the main display is 0,0); a step with
"screen": N uses pixels of screen N's last --look picture instead, mapped through the snapshot --look saved for this ask."""
import json, os, tempfile

DRAW = 'atticus --draw'
LOOK = 'atticus --look'

# The system prompt carries only this; the full guide comes from `atticus --draw --help`, so asks that never draw pay nothing for it.
DRAWING = f"""
UI where/how asks, even if known: point, not tell; draw/explain on screen: diagram the idea, not marks. Run {LOOK} && {DRAW} --help (one call), then {DRAW} once."""

HELP = f"""
Read the --look pictures from this ask first (drawing refuses without them), then ONE {DRAW} '<JSON>' with every step.
JSON (a locate step): {{"steps":[{{"id":"1","say":"short spoken instruction","screen":1,"shapes":[{{"kind":"rect","rect":[x,y,w,h],"color":"red"}}],"hit":[x,y,w,h],"wait":true}}]}}.
1-12 steps. Pick the kind from the ask, not from what is on screen:
- Locate (where is it, where/how do I do it in this app): marks on the target. Fit the mark to the thing: rect around buttons, fields, rows; circle on small icons; arrow + label for far or hidden ones. Tutorials put "hit" (the area to click) and "wait":true on each step, so the guide moves on when the user clicks there (or says "next"). User clicks; never act for them.
- Explain (explain, teach, compare, how something works, draw something; any subject, math, code, a graph): draw the idea itself so it teaches without the speech: labeled parts, steps joined by arrows, a side-by-side comparison, or worked text lines. Put it in clear space (an empty area of the picture), not over the text it explains. Trace an on-screen figure when there is one; otherwise draw your own, using the page's labels and numbers (your own example only if none fits). One small mark may link it to the source; marks around on-screen words are not an explanation. Omit hit and wait; every step needs say; parts draw on top of earlier steps and captions accumulate.
Coordinates: put "screen":N on the step (never in a shape) and use pixels of screen N's --look picture. Without "screen" they are Windows screen pixels (top-left of the main screen is 0,0).
Speech follows the read-aloud setting. Drawn text is plain Unicode, no LaTeX.
Shape keys, no others: rect/circle "rect":[x,y,w,h] (circle may be an ellipse; add "fill":true for a translucent diagram part at those exact bounds); arrow/line "points":[[x,y],[x,y]] (exactly 2; path for more); path "points":[[x,y],...]; label "at":[x,y],"text" (short tag), optional "size":"s"|"m"|"l"; pointer "at"; text "text" (one large line; every step's text lines stack on one panel, whiteboard style: use them for practice problems and worked math, one step per line; "size":"s"|"m"|"l", default m; the first may set "at":[x,y], the panel's top-left in empty space); any shape "color": red/blue/green/yellow/white. Optional step "timeout" 5-120 (seconds a locate step stays). Drawings stay through later asks until Close, Esc or your next draw, which replaces them. Don't repeat captions or claim it appeared unless this returns "Guide shown". Re-read after layout changes.
"""

COLORS = ('red', 'blue', 'green', 'yellow', 'white')


def drawing(data, mappings=None):
    """Validate the model boundary and convert screenshot pixels to Windows screen pixels. -> {'steps': [...], 'teaching': bool}"""
    def number(v):
        if type(v) not in (int, float) or not -100000 <= v <= 100000: raise ValueError('Invalid drawing coordinate')
        return v
    def vector(v, count):
        if not isinstance(v, list) or len(v) != count: raise ValueError('Invalid drawing coordinates')
        return [number(x) for x in v]
    if not isinstance(data, dict) or set(data) != {'steps'} or not isinstance(data['steps'], list) or not 1 <= len(data['steps']) <= 12:
        raise ValueError('Use {"steps":[...]} with 1-12 steps')
    steps = []
    for step in data['steps']:
        if not isinstance(step, dict) or set(step) - {'id', 'say', 'shapes', 'wait', 'hit', 'screen', 'timeout'}:
            raise ValueError('Invalid drawing step' + (' (no "target" on Windows: use "hit" and "wait")' if isinstance(step, dict) and 'target' in step else ''))
        out = {**step}
        for key, limit in [('id', 80), ('say', 1000)]:
            if key in step and (not isinstance(step[key], str) or not 1 <= len(step[key]) <= limit): raise ValueError('Invalid ' + key)
        if 'wait' in step and type(step['wait']) is not bool: raise ValueError('Invalid wait')
        timeout = number(step.get('timeout', 60))
        if not 5 <= timeout <= 120: raise ValueError('Timeout must be 5-120 seconds')
        out['timeout'] = timeout
        mapping = None
        if 'screen' in step:
            if type(step['screen']) is not int: raise ValueError('Invalid screen')
            mapping = (mappings or {}).get(str(step['screen']))
            if not mapping: raise ValueError('Read --look again before drawing in picture pixels')
        def point(v):
            x, y = vector(v, 2)
            if mapping:
                fx, fy, fw, fh = mapping['frame']; pw, ph = mapping['pixels']
                if not 0 <= x <= pw or not 0 <= y <= ph: raise ValueError('Point outside screenshot')
                return [round(fx + x * fw / pw, 1), round(fy + y * fh / ph, 1)]
            return [x, y]
        def rect(v):
            x, y, w, h = vector(v, 4)
            if w <= 0 or h <= 0: raise ValueError('Drawing rectangle must have positive size')
            a, b = point([x, y]), point([x + w, y + h])
            return a + [round(b[0] - a[0], 1), round(b[1] - a[1], 1)]
        if 'hit' in step: out['hit'] = rect(step['hit'])
        shapes = step.get('shapes')
        if not isinstance(shapes, list) or not 1 <= len(shapes) <= 40: raise ValueError('Use 1-40 shapes per step')
        out['shapes'] = []
        for shape in shapes:
            if not isinstance(shape, dict) or set(shape) - {'kind', 'rect', 'at', 'points', 'text', 'color', 'size', 'fill'}: raise ValueError('Invalid shape')
            kind = shape.get('kind')
            if kind not in ('rect', 'circle', 'arrow', 'line', 'path', 'label', 'pointer', 'text'): raise ValueError('Invalid shape kind')
            fields = {'rect', 'fill'} if kind in ('rect', 'circle') else {'at', 'text', 'size'} if kind in ('label', 'text') else {'at'} if kind == 'pointer' else {'points'}
            if set(shape) - {'kind', 'color'} - fields: raise ValueError('Coordinates do not match shape kind')
            if shape.get('color', 'red') not in COLORS: raise ValueError('Invalid color')
            if 'fill' in shape and type(shape['fill']) is not bool: raise ValueError('Fill must be true or false')
            value = {**shape}
            if kind in ('rect', 'circle'):
                if 'rect' not in shape: raise ValueError('Shape needs rect')
                value['rect'] = rect(shape['rect'])
            elif kind == 'text':
                if 'at' in shape: value['at'] = point(shape['at'])
                if not isinstance(shape.get('text'), str) or not 1 <= len(shape['text']) <= 300: raise ValueError('Invalid text')
                if shape.get('size', 'm') not in ('s', 'm', 'l'): raise ValueError('Text size is s, m or l')
            elif kind in ('pointer', 'label'):
                if 'at' not in shape: raise ValueError('Shape needs at')
                value['at'] = point(shape['at'])
            else:
                points = shape.get('points')
                if not isinstance(points, list) or not 2 <= len(points) <= (256 if kind == 'path' else 2): raise ValueError('Invalid path')
                value['points'] = [point(v) for v in points]
            if kind == 'label' and (not isinstance(shape.get('text'), str) or not 1 <= len(shape['text']) <= 120): raise ValueError('Invalid label')
            if kind == 'label' and shape.get('size', 'm') not in ('s', 'm', 'l'): raise ValueError('Label size is s, m or l')
            out['shapes'].append(value)
        if step.get('wait') and 'hit' not in step: raise ValueError('Waiting needs a hit rectangle')
        out.pop('screen', None); steps.append(out)
    teaching = all(not step.get('wait') and 'hit' not in step for step in steps)
    if teaching and any(not step.get('say') for step in steps): raise ValueError('Teaching steps need a visible say caption')
    return {'steps': steps, 'teaching': teaching}


def mappings(monitors, width=1280):
    """--look's pictures -> {screen number: {'frame': [x, y, w, h], 'pixels': [w, h]}}, the same resize winctl.capture does."""
    out = {}
    for i, m in enumerate(monitors, 1):
        w, h = m['w'], m['h']
        out[str(i)] = {'frame': [m['x'], m['y'], w, h], 'pixels': [width, round(h * width / w)] if w > width else [w, h]}
    return out


def context(value):
    """The guide still on screen -> prompt context, so a follow-up can explain or build on it (screenshots leave it out)."""
    if not isinstance(value, dict) or not isinstance(value.get('steps'), list) or not value['steps']:
        return ''
    steps = json.dumps(value['steps'], ensure_ascii=False, separators=(',', ':'))
    if len(steps) > 4000:   # many shapes: the captions still say what it shows
        steps = json.dumps([s.get('say', '') for s in value['steps'] if isinstance(s, dict)], ensure_ascii=False)[:2000]
    shown = value.get('shown') if type(value.get('shown')) is int else len(value['steps'])
    return (f'Your drawing is still on screen, step {shown} of {len(value["steps"])} (Windows screen pixels; data): {steps}\n'
            f'It stays. When the follow-up calls for more drawing, run {LOOK} && {DRAW} --help (one call), then one {DRAW} with the steps to keep plus new ones; that replaces it.\n')



def point_context(value):
    """The user's own mark from Point at your screen (or a click on one of the drawing's marks) -> (prompt context, [(crop, label)])."""
    if not isinstance(value, dict):
        return '', []
    number = lambda v: type(v) in (int, float) and abs(v) <= 100000
    bounds, path = value.get('bounds'), value.get('path')
    if not isinstance(bounds, list) or len(bounds) != 4 or not all(map(number, bounds)) or bounds[2] <= 0 or bounds[3] <= 0:
        return '', []
    if not isinstance(path, list) or not 1 <= len(path) <= 256 or any(not isinstance(p, list) or len(p) != 2 or not all(map(number, p)) for p in path):
        return '', []
    mark = value.get('mark', '')   # one of Atticus's own marks the user clicked
    mark = mark[:1000] if isinstance(mark, str) else ''
    image, shots = value.get('image', ''), []
    if isinstance(image, str) and image:   # only Atticus.exe's own crop in Temp, never a path from anywhere else
        real, temp = os.path.realpath(image), os.path.realpath(tempfile.gettempdir()) + os.sep
        if os.path.normcase(real).startswith(os.path.normcase(temp)) and os.path.basename(real).startswith('atticus-point-') and real.endswith('.jpg') and os.path.isfile(real):
            shots = [(real, 'The user selected this mark of your drawing.' if mark else
                      'User-drawn mark: the red point or path identifies the part of this cropped screen region they mean.')]
    if not shots and not mark:
        return '', []
    kind = 'point' if len(path) == 1 else 'path'
    context = (f'User selected a mark of your drawing (quoted data): {json.dumps(mark, ensure_ascii=False)}, screen region {json.dumps(bounds)}.' if mark
               else f'User mark (trusted geometry): {kind} in Windows screen region {json.dumps(bounds)}.')
    return context + '\n', shots
