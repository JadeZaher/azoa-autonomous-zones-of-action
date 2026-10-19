# Dashboard layout components

## Audience

Every navigation group declares an audience. Individual items may narrow it; filtering happens before group labels and separators render. The ordinary account profile currently provides no trustworthy per-user operator permission for this sidebar. `NEXT_PUBLIC_AZOA_OPERATOR_CONSOLE=true` is therefore only a console-instance display opt-in (OI-84), never authorization. An unset or blank flag hides operator-only entries. Server policies remain authoritative.

## Accessible navigation

The icon-only mobile sheet trigger is named “Open navigation” and the sheet is titled “Navigation.” The decorative Menu icon is hidden from the accessibility tree. The trigger provides a minimum 44-pixel target; Base UI owns expanded-state and dialog keyboard behavior. The dashboard's independently scrolling main region is keyboard-focusable and has a visible focus outline so enlarged-text users can scroll it without a pointer.
