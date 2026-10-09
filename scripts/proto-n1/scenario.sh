#!/usr/bin/env bash
# Throwaway: start the app, build the sample workflow, run it to its six states, and add the never-run review.
SP=/tmp/claude-1000/-home-mano-repo-iDevelop/ba3f5004-1b4f-437c-a6c1-7505a103a929/scratchpad/n1
H=$SP/tools/h.sh
$H stop >/dev/null 2>&1
$H start
$H cmd "size 1600 1000" "theme light" "scenario" "wait 300"
( cd $SP/run/project && HOME=$SP/run/home git add -A && HOME=$SP/run/home git commit -q -m workflow )
$H cmd "runworkflow" "wait 9000" "add-review" "wait 800" "state"
