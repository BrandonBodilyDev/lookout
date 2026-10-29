# Running the drone pipeline on real footage

The pipeline in `src/Lookout.Drone` has only been tested on synthetic footage. This runbook covers the first real runs: your own fire-pit flight, and public footage of prescribed burns. Steps marked **verify** depend on details this repo could not confirm without the hardware or the files.

## Before you fly

- **Never fly near a wildfire.** Drones near fires ground firefighting aircraft, and flying inside a wildfire temporary flight restriction is a federal crime.
- **Legal fire only.** A backyard fire pit or campfire ring, where open fires are allowed that day. Check local burn restrictions first.
- **FAA recreational rules.** Carry your TRUST certificate, stay under 400 ft, and keep the drone in sight. Check airspace with an FAA-approved app such as Aloft; B4UFLY is retired. St. George's airport (SGU) is Class E surface area today.
- **Stay sub-250 g.** No accessories that push the Mavic Mini over 250 g.
- **No modified firmware or apps.** The original Mavic Mini reached end of service in April 2026, so a bricked unit cannot be repaired.

## 1. Record the subtitle track

1. In DJI Fly, open camera settings and turn on **Video Subtitles**. **Verify** that the option is still there on your firmware.
2. Record at 1080p. The camera model assumes 1080p uses the full sensor width.
3. Fly a few straight passes over the fire pit at 30 to 60 m, gimbal straight down (−90°), heading north. A fixed heading and pitch mean you need no flight log.
4. Copy the MP4 off the microSD card. The Mavic Mini embeds the subtitle track in the video.

Check the track:

```bash
dotnet run --project src/Lookout.Sim -- drone track DJI_0001.MP4
```

It should report about one sample per second, with heights close to your flying height. If ffmpeg finds no subtitle stream, look for a sidecar `.SRT` file next to the video and pass it with `--srt`.

## 2. Calibrate the field of view

The 71.5° horizontal field of view is derived from the spec sheet, not measured.

1. Lay out a known length on open ground, such as a 30 m tape measure or two cones 20 m apart.
2. Hover straight above it, gimbal at −90°, at a measured height, for example 30 m from the subtitle track.
3. Grab a frame and measure the target's length in pixels.
4. Compute: `HFOV = 2 · atan( (image_width_px · target_m / target_px) / (2 · height_m) )`.
5. Pass the result with `--hfov`, and update `CameraModel.MavicMini1080p` and `docs/assumptions.md` if it differs by more than a degree or two.

## 3. Survey the flight

Your fire pit is not on the demo terrain, so make a scenario for your location first. Copy the demo YAML, set `origin` to your yard, put a route and safety zone nearby, and build its terrain grid:

```bash
dotnet run --project src/Lookout.Sim -- dem scenarios/backyard.yaml scenarios/terrain/backyard.asc -500 500 -500 500 10
```

Then survey:

```bash
dotnet run --project src/Lookout.Sim -- drone survey scenarios/backyard.yaml DJI_0001.MP4 --heading 0 --pitch -90 --hfov 71.5 --out backyard-front.geojson
```

Open `backyard-front.geojson` in geojson.io and compare the burned area with where the pit really is. Expect errors of the order of 10 m from the 4-decimal GPS alone.

**If heading or pitch varied in flight,** export the flight log from Airdata as CSV and pass `--log airdata.csv`. **Verify** the column names: the reader expects `time(millisecond)`, `compass_heading…`, `gimbal_pitch…` and `isVideo`.

## 4. Public prescribed-burn footage

**FLAME 2** (Northern Arizona University, IEEE DataPort) has 1080p and 4K drone video of a spreading prescribed fire. It needs a free IEEE account, and the video pairs are about 13.6 GB. It ships without a DJI subtitle track, so supply a pose: either write a small SRT with the documented flight altitude and position, or use `drone survey` with a scenario centred on the burn. **Verify** the altitude and camera angle from the dataset paper before trusting the geometry.

**Boreal Forest Fire** (Finnish Geospatial Research Institute, 2025, CC BY 4.0) has 4K 30-second smoke clips with labels. It is good for testing detection, less so for mapping, because poses are not included.

Do not commit any dataset to this repo. Credit each one in anything you publish.

## 5. A trained detector

The color detector is a baseline. For real smoke and glare, use a trained model with a permissive license.

1. Choose Apache-2.0 weights, for example an RF-DETR fire model on Hugging Face. Avoid Ultralytics YOLO weights: they are AGPL-3.0, which would change this project's license.
2. Export to ONNX with the model's own tooling, in a Python virtual environment kept outside this repo. **Verify** the export command for the version you install; for RF-DETR it is a method on the model object.
3. Check the exported graph: one image input `[1, 3, H, W]`, a box output `[1, N, 4]` as normalized centre and size, and a class-logit output `[1, N, C]`. That is what `OnnxFireDetector` reads.
4. Run the survey with `--model fire.onnx`. Add `--fire-classes 0,1` if the model has classes other than fire and smoke.

Report fp32 against int8 latency and accuracy once quantized; that comparison is the on-device story for Phase 3.

## 6. Into a live incident

With the stack running (`docker compose up`), publish a surveyed front into the live incident:

```bash
MQTT_USER=field MQTT_PASSWORD=lookout-dev-field dotnet run --project src/Lookout.Sim -- drone survey scenarios/backyard.yaml DJI_0001.MP4 --publish <incident-id>
```

The dashboard labels the fire front with its source, so it is always clear whether the engine is working from truth, a virtual camera, or real footage.
