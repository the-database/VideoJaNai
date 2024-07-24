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
        sys.exit(1)

vs.core.std.LoadPlugin(os.path.abspath(os.path.join(__file__, os.path.pardir, os.path.pardir, os.path.pardir, r"python\vapoursynth64\plugins\vstrt.dll")))
vs.core.std.LoadPlugin(os.path.abspath(os.path.join(__file__, os.path.pardir, os.path.pardir, os.path.pardir, r"python\vapoursynth64\plugins\vsort.dll")))
vs.core.std.LoadPlugin(os.path.abspath(os.path.join(__file__, os.path.pardir, os.path.pardir, os.path.pardir, r"python\vapoursynth64\plugins\vsncnn.dll")))
# raise ValueError(vs.core.trt)
clip = vs.core.ffms2.Source(source=args["video_path"])
slot = int(args["slot"])
animejanai_core.run_animejanai_with_keybinding(clip, clip.fps, slot)