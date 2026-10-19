# Application surfaces

## Theme

`globals.css` owns semantic OKLCH tokens. Light surfaces use a deeper orange primary with pale text; dark surfaces use a lighter orange primary with ink text. Primary also appears as small link/status text, so button foregrounds and surrounding backgrounds must be considered together. The previous dark primary/pale foreground pair was approximately 4.03:1; the revised dark primary/ink pair is approximately 7.11:1, and the light primary/pale pair approximately 6.18:1. These are palette calculations, not a browser accessibility verdict.

`.azoa-light-surface` resets the complete light token set inside a dark document. The operator login deliberately uses paper surfaces, so it must apply this class and `text-foreground`; hard-coded paper backgrounds with inherited dark-theme pale label text are unreadable. The public landing chrome has its own paper/ink CSS and a darker accent, documented beside that component. Automated axe reruns and visual review remain required after palette changes.
