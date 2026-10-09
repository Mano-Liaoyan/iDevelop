#!/usr/bin/env bash
# Throwaway: drive option A's card with the real pointer and keyboard: expand, type an answer with canvas shortcut
# letters in it, drag inside the answer box, and send with Ctrl+Enter. Prints what the canvas reports after each step.
SP=/tmp/claude-1000/-home-mano-repo-iDevelop/ba3f5004-1b4f-437c-a6c1-7505a103a929/scratchpad/n1
H=$SP/tools/h.sh
OUT=$SP/a/interaction
mkdir -p $OUT
X="DISPLAY=:131 xdotool"
OPT=${OPT:-a} $SP/tools/scenario.sh | tail -1
$H cmd "select none" "collapseall" "focus Implement atomic" "wait 400" >/dev/null
where() { rm -f $SP/run/probe/ack; printf 'where %s\n' "$1" > $SP/run/probe/cmd; for i in $(seq 1 50); do [ -f $SP/run/probe/ack ] && break; sleep 0.2; done; cat $SP/run/probe/ack; echo; }
echo "before: $(where 'CardExpand Implement atomic')"
read -r _ x y _ < <(where 'CardExpand Implement atomic')
DISPLAY=:131 xdotool mousemove $x $y click 1; sleep 1.2
$H shotkeep $OUT/1-expanded.png 1600 1000
read -r _ x y _ < <(where 'CardAnswer Implement atomic')
DISPLAY=:131 xdotool mousemove $x $y click 1; sleep 0.4
DISPLAY=:131 xdotool type --delay 40 "No fallback. Refuse to save on network drives, and say so."
sleep 0.6
echo "typed: $(where 'CardAnswer Implement atomic')"
$H shotkeep $OUT/2-typed.png 1600 1000
# A press and drag that starts in the answer box selects text; it must not move the card.
DISPLAY=:131 xdotool mousemove $((x-100)) $y mousedown 1 mousemove $((x-20)) $((y+30)) mousemove $((x+60)) $((y+60)) mouseup 1; sleep 0.6
echo "dragged: $(where 'CardAnswer Implement atomic')"
$H shotkeep $OUT/3-dragged.png 1600 1000
DISPLAY=:131 xdotool key ctrl+Return; sleep 2
$H shotkeep $OUT/4-sent.png 1600 1000
$H stop
