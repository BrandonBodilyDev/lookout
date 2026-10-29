# Assumptions and default parameters

Every number here is a knob in the scenario files, never hard-coded in the engine. Defaults come from published rules of thumb, not from field validation, and the README should say so. Items marked **ASK** are questions for Brandon's dad (working firefighter). When he answers, replace the default, note the date, and keep the old value in the table for honesty.

## Crew movement
| Parameter | Default | Source / note |
|---|---|---|
| Travel rate vs slope | Sullivan et al. 2020 Lorentz model, **moderate** tertile: v = c·(1/(π·b·(1+((θ−a)/b)²))) + d + e·θ, θ in degrees, v in m/s, with a = −2.8292, b = 20.9482, c = 77.6346, d = 0.2228, e = −0.0004. Gives 1.38 m/s flat, 0.90 at +15°, 0.55 at +30°. | GPS-tracked Type 1 hotshot crews carrying gear on trails (*Fire* 3(3):52, https://www.mdpi.com/2571-6255/3/3/52). Changed 2026-10-28 from Tobler × 0.7 (0.98 flat, 0.38 at +15°), which was 2.4–4× too slow uphill. Verify the formula form against the paper before coding. **ASK:** realistic pace with full gear on a fireline, and whether crews drop packs once a trigger point is hit. |
| Conservative setting | Sullivan **low** tertile: a = −3.3717, b = 25.8255, c = 92.6594, d = −0.1624, e = 0.0019 | The IRPG plans escape time on the slowest person. Exposed as a scenario knob. |
| Off-trail factor | × 0.5 | Yarnell Hill SAIR: off-trail travel "less than half" of on-trail speed. |
| Packs dropped | × 1.2 | Butler 2014. Sullivan 2020 notes its functions do not apply when packs are dropped. |
| Regroup overhead before moving | 0 min | **ASK:** does the crew boss hold for a head count or a radio call before moving? |

## Fire spread (elliptical model)
| Parameter | Default | Source / note |
|---|---|---|
| Head-fire rate of spread R | 10–30 m/min before the wind shift, 60–150 after, 150–300 in a blowup | Sourced ranges (GTR-153 Table 5; grass 20% rule, Cruz, Alexander & Kilinc 2022; South Canyon and Yarnell reports). Changed 2026-10-28 from 20–60. R is an input, never computed from fuel moisture. |
| Length-to-breadth ratio | LB = 0.936·e^(0.2566·U) + 0.461·e^(−0.1548·U) − 0.397, U = midflame wind in m/s, capped at 8 | FARSITE eq. 13 (Finney 1998, RMRS-RP-4 p. 7), after Anderson 1983. Backing R_B = R_H(1−e)/(1+e), rate at angle γ R_γ = R_H(1−e)/(1−e·cos γ), e = √(LB²−1)/LB (Andrews 2018, GTR-371 §6.2). |
| Slope effect on spread | Implemented per grid edge: spread along an edge is multiplied by 2^(θ/10) for θ degrees of upslope along that edge, capped at 30°, with no reduction downslope | McArthur 1967 / Noble 1980 rule, not fully validated; Cheney 2001 via Butler 2014 gives doubling at about 18% and 36% grade. Applying it per edge means the head bends toward upslope on its own; no separate wind-plus-slope vector is computed. Alternative for later: Rothermel slope factor φs = 5.275·β^−0.3·tan²φ. |
| Spotting | none | Out of scope; the README says so. |

## Alert thresholds
| Parameter | Default | Note |
|---|---|---|
| Downgrade hold | 2 min | A lower level must hold this long before it is sent; escalations go out at once. Stops a margin sitting on a threshold from flapping over the link (alarm fatigue). |
| Safety buffer added to crew travel time | 10 min | **ASK:** how many minutes of warning actually changes a decision? |
| WATCH | margin under 30 min | Fryer, Dennison & Cova 2013 used 15/30/45-min buffers; ERI uses 10/20/30-min frames. Changed 2026-10-28 from 20. |
| WARNING | margin under 15 min | Changed 2026-10-28 from 10. |
| CRITICAL | margin under 5 min, or route already cut | IRPG: blowup to burnover "can be as little as 5" minutes. |
| COMMS LOST | stale at 5 min, escalate to the IC at 15 min (AFF dispatch timer) | **ASK:** how long a crew goes without radio contact before anyone gets worried. |
| Rule 2 sector (Watch Out #9) | downwind ±45°, within 500 m of the crew | **ASK:** at what distance unburned fuel between you and the fire starts to matter. |

## Weather from the National Weather Service
| Parameter | Default | Note |
|---|---|---|
| Wind source | Field observations; NWS optional per incident | NWS gives the latest observation at the nearest station (KSGU for the demo area), refreshed every 10 minutes. |
| 10 m wind to 20 ft wind | × 0.87 | NWS reports a 10 m wind; fire models use the 20 ft (6.1 m) reference height. |
| Wind adjustment factor (20 ft to midflame) | 0.4 | Partially sheltered surface fuels. Fully exposed grass would be about 0.4 to 0.5; under a closed canopy, 0.1 to 0.2. |
| Head-fire rate under NWS wind | the field's current value | NWS cannot provide a spread rate, so it stays a field or scenario input. |

## Link budget
| Parameter | Default | Note |
|---|---|---|
| Max uplink message (crew → server) | 340 bytes | Iridium SBD mobile-originated limit (9602/9603 class modems). |
| Max downlink message (server → crew) | 270 bytes | Iridium SBD mobile-terminated limit. Alerts are downlink, so 270 is the real cap. |
| Crew position report | every 60 s, 16 bytes binary (crew id, lat/lon as int32 ×1e-7, time, battery) | AFF's floor is one report per 2 min. Changed 2026-10-28 from 20 bytes. |
| Alert repeat | every 60 s (scenario time) until acknowledged; escalate to the IC after 3 min without an acknowledgement | SBD needs about 40 s between sessions, so the governor also spaces any two transmissions to one device by 40 s. Pattern copied from inReach SOS. |
| Simulated link | 2.4 kbps, one message per 20 s per device | The governor queues, dedupes, and sends highest severity first. |

## Questions for Dad (one sitting, about 30 minutes)
1. What does a lookout actually watch for, in order of priority? What makes them call the crew off the line?
2. How is a trigger point chosen today, and who decides it? Is it a place, a time, or a fire behavior?
3. Realistic crew pace: flat, uphill, downhill, with gear. Do they drop packs?
4. How many minutes of warning is useful? At what point is a warning too late to matter?
5. How often does the crew check in by radio, and how long before silence becomes a concern?
6. Watch Out #9: how close does unburned fuel between you and the fire have to be before it changes the plan?
7. When the wind shifts, what is the first sign on the ground, and how long between that sign and the fire changing direction?
8. What would make him trust an automated warning, and what would make him ignore it?
9. Any story (South Canyon, Yarnell, or a local one) he would want the demo scenario to resemble, told respectfully.

## Drone survey
| Parameter | Default | Source / note |
|---|---|---|
| Mavic Mini horizontal field of view | 71.5° (1080p video assumed to use the full sensor width) | Derived from the spec sheet: 83° diagonal, 24 mm equivalent, 1/2.3" sensor. Not calibrated: fly over a ground target of known size and correct it. |
| Subtitle-track position precision | 4 decimal places, about 11 m | Mavic Mini .SRT. Newer DJI drones write 6 decimals. |
| Camera height | take-off ground elevation + SRT "H" | "H" is height above the take-off point (barometric). Ground under the take-off point comes from the terrain grid. |
| Flame rule | R > 135, R ≥ G > B, S ≥ (255 − R) · 60 / 135, and Cr − Cb ≥ 40 | Chen, Wu and Chiou 2004 (RGB) plus Celik and Demirel 2009 (chrominance). Chen alone accepts dry tan grass. |
| Burn-scar rule | max(R, G, B) < 70 and saturation < 70, only when connected to flame | Keeps shadows from being mapped as burned ground. |
| Minimum detection region | 12 pixels | Drops sensor noise and isolated red specks. |
| Cell marking | at least 2 fire pixels in a 30 m cell in one frame | Pixels are sampled every second row and column. |
| ONNX detector score threshold | 0.4 | For DETR-family models with sigmoid class scores. Tune on held-out aerial frames. |
| Virtual survey camera (simulation) | 1,800 m above ground, straight down, 800 × 600 px | About 2.6 × 1.9 km per frame, about 3 m per pixel. Represents an incident-awareness aircraft or Group 3 UAS, not a Mavic Mini. |
