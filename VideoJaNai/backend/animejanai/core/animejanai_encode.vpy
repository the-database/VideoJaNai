import sys, os, pathlib
import vapoursynth as vs

sys.path.append(str(pathlib.Path().absolute()))

import animejanai_core

args = dict(globals())

required_args = {'video_path', 'slot'}

args = dict(globals())

for arg in required_args:
    if arg not in args:
        raise ValueError(f"{arg} must be defined in globals")

clip = vs.core.ffms2.Source(source=args["video_path"])
slot = int(args["slot"])
animejanai_core.run_animejanai_with_keybinding(clip, clip.fps, slot)