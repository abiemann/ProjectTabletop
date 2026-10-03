# Shared board improvements

Apply generally useful improvements to interaction, hand acquisition, spotlight
behavior, and rendering through the shared board code so all applicable boards
benefit. Do not limit a shared improvement to the board where a problem was first
reported. Preserve intentional board-specific behavior, such as gesture-test
feedback, game rules, capture regions, and animation timing. Verify representative
boards, including the main menu and navigation controls.

# Normal hand orientation

Regular board operation uses palm-down hands: the camera sees the back of the
hand and fingernails. Acquire naturally grouped fingers directly; do not require
users to spread their fingers first or turn their palm toward the camera.
Keep the deliberate sideways-index selection gesture. A palm-facing-camera
Properties gesture is only a possible future feature, not a current requirement.

# Long-press detection

Broken lettering is the golden rule for detecting a long-press button. Require
actual corruption of the stationary caption's letter shapes. Intact lettering
must cancel partial hold progress and count as clear even when its colour,
brightness, or underlying surface reflectance changes. Button borders, animation,
and detected hand landmarks alone must never count as a long press. Apply this
rule through the shared hold-button code on every board.

# Natural graphics and effect boundaries

The user prefers mature, high-quality graphics with natural silhouettes and
motion. Do not disguise decorative effects with broad rectangular opacity fades
or blurred edges simply to fit a layout box. Shape the effect itself: for example,
title flames should begin as small, irregular tongues at the sides and build
rapidly toward the lettering. Allow suitable wisps or plasma to rise beyond a
decorative title plaque rather than squeezing the fire into its rectangle.
Keep intentional physical containers and interaction references protected:
reel pixie dust stays inside the reel window, and effects must not obscure
stationary control captions or jackpot values.
