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
