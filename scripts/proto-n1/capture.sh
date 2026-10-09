#!/usr/bin/env bash
# Throwaway: capture one option's scenes in both themes at the small and the large window. Usage: capture.sh a|b|c
SP=/tmp/claude-1000/-home-mano-repo-iDevelop/ba3f5004-1b4f-437c-a6c1-7505a103a929/scratchpad/n1
H=$SP/tools/h.sh
O=$1
OUT=$SP/$O
mkdir -p $OUT/dumps
rm -f $OUT/*.png
OPT=$O $SP/tools/scenario.sh | tail -1
shot() { # name W H
  $H cmd "dump $1" >/dev/null
  cp $SP/run/probe/$1.json $OUT/dumps/
  $H shot $OUT/$1.png $2 $3
}
focustop() { if [ $O = b ]; then echo "focus note:Write the format"; else echo "focus Write the format"; fi; }
focusone() { if [ $O = b ]; then echo "focus note:Implement atomic"; else echo "focus Implement atomic"; fi; }
for theme in light dark; do
  $H cmd "theme $theme" "collapseall" "select none" >/dev/null
  # Large window: fit the whole graph, never above 100%.
  $H cmd "size 1600 1000" "wait 700" "collapseall" "wait 400" "fit" "wait 300" "fit" >/dev/null
  shot $theme-large-compact 1600 1000
  if [ $O = b ]; then $H hover "Write the format"; $H cmd "dump $theme-large-peek" >/dev/null; cp $SP/run/probe/$theme-large-peek.json $OUT/dumps/; $H shotkeep $OUT/$theme-large-peek.png 1600 1000; fi
  $H cmd "expand Implement atomic" "wait 900" "fit" "wait 300" "fit" >/dev/null
  shot $theme-large-one 1600 1000
  $H cmd "expand Design" "expand Write the format" "expand Write crash" "wait 1200" "fit" "wait 300" "fit" >/dev/null
  shot $theme-large-several 1600 1000
  $H cmd "expandall" "wait 1200" "fit" "wait 300" "fit" >/dev/null
  shot $theme-large-all 1600 1000
  # Small window: at 900 by 600 the canvas is about 340 px wide, so a person works at 100% and pans to the card.
  $H cmd "collapseall" "size 900 600" "wait 900" "focus Implement atomic" "wait 300" >/dev/null
  shot $theme-small-compact 900 600
  if [ $O = b ]; then $H cmd "focus Write the format" "wait 300" >/dev/null; $H hover "Write the format"; $H cmd "dump $theme-small-peek" >/dev/null; cp $SP/run/probe/$theme-small-peek.json $OUT/dumps/; $H shotkeep $OUT/$theme-small-peek.png 900 600; fi
  $H cmd "expand Implement atomic" "wait 900" "$(focusone)" "wait 300" >/dev/null
  shot $theme-small-one 900 600
  $H cmd "expand Design" "expand Write the format" "expand Write crash" "wait 1200" "$(focustop)" "wait 300" >/dev/null
  shot $theme-small-several 900 600
done
# The marked places for GitHub and comments, off in every other shot.
$H cmd "theme light" "collapseall" "size 1600 1000" "wait 700" "placeholders on" "expand Design" "wait 900" "focus $( [ $O = b ] && echo note:Design || echo Design )" "wait 300" >/dev/null
if [ $O = c ]; then $H cmd "tab Design later" "wait 500" >/dev/null; fi
shot light-large-placeholders 1600 1000
$H cmd "placeholders off" "collapseall" >/dev/null
if [ $O = c ]; then
  $H cmd "expand Implement atomic" "tab Implement atomic agent" "wait 900" "focus Implement atomic" "wait 300" >/dev/null
  shot light-large-tab-agent 1600 1000
fi
$H stop
ls $OUT/*.png | wc -l
