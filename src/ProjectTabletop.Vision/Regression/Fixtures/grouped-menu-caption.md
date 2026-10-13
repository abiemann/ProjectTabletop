# Grouped-finger menu caption fixture

The grouped-menu regression reuses the checked-in
`menu-control-reflectance-fit-camera.png`,
`menu-control-reflectance-fit-expected.png`, and
`menu-control-reflectance-fit.json` fixtures. Their source and permissions are
described in [menu-control-reflectance-fit.md](menu-control-reflectance-fit.md).
These images contain only app-generated artwork, rendered by the isolated native
compositor on October 5, 2026. No camera photograph, person, room, or private
diagnostic capture supplies fixture pixels.

The regression constructs its occluders in memory over the Crown & Deed menu
caption (control index 4). Four rounded opaque strips represent finger coverage;
they are a geometric obstruction test, not photographs or a hand-model accuracy
test. Their BGRA colour is `(75, 95, 185, 255)`. Each strip is 18 native pixels
wide and 95 pixels long, with fingertip height offsets of 8, 0, 7, and 20 pixels.
The middle tip is centred horizontally on the caption and begins three quarters
of the caption height above its top. The grouped strips have 2-pixel gaps; the
spread comparison has 18-pixel gaps. The fingers retain the same individual size.
An identically sized ellipse supplies the lower palm patch in both cases.

Before the correction, the grouped fixture physically changed 15.95% of the
control interior and 36.36% of its caption rectangle. Its two most damaged fixed
horizontal caption sectors correlated at 0.4354 and 0.3805, while the complete
caption correlated at 0.8020. Only one sector crossed the former 0.40 severe-loss
cutoff, so the detector reported uncertain lettering, zero local damage, and no
acquisition hint despite repeated fresh frames. The spread comparison changed
the same 36.36% of the caption rectangle; its whole-caption correlation fell to
0.7734, allowing structural corruption to confirm and acquisition to proceed.

Those percentages describing physical replacement are fixture measurements,
not substitutes for the detector's independently measured control and caption
coverage. The intended regression preserves both 7% evidence floors, fresh-frame
confirmation, and the requirement for actual damaged letter shapes. Intact
lettering under colour or brightness changes must remain clear; it must not
inherit the grouped obstruction's confirmation or produce a hold.

This fixture isolates local caption-shape recognition. It does not establish
live palm-down hand acquisition, projector-lighting quality, gesture recognition,
or physical-board reliability. Those require separate camera observations.
