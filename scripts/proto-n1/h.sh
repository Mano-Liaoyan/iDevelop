#!/usr/bin/env bash
# Throwaway N1 harness: Xvfb + the prototype app with fake clients and a scratch project, driven by a command file.
SP=/tmp/claude-1000/-home-mano-repo-iDevelop/ba3f5004-1b4f-437c-a6c1-7505a103a929/scratchpad/n1
WT=/home/mano/repo/iDevelop.wt/n1
RUN=$SP/run
DISP=:131
DOTNET=/home/mano/.dotnet/dotnet
AGENT=$WT/tests/IDevelop.FakeAgent/bin/Release/net10.0/IDevelop.FakeAgent.dll
FIX=$WT/tests/Shared/Fixtures
case "$1" in
  start)
    rm -rf "${RUN:?}"; mkdir -p $RUN/xdg $RUN/home $RUN/probe $RUN/bin $RUN/gates $RUN/evidence $RUN/project
    printf '[user]\n\tname = N1 Prototype\n\temail = n1@example.test\n[init]\n\tdefaultBranch = main\n[maintenance]\n\tauto = false\n' > $RUN/home/.gitconfig
    python3 $SP/tools/fakes.py $RUN $AGENT $FIX $DOTNET
    ( cd $RUN/project && HOME=$RUN/home git init -q && printf '# Storage\n\nThe workflow storage change.\n' > README.md \
      && HOME=$RUN/home git add README.md && HOME=$RUN/home git commit -q -m base )
    Xvfb $DISP -screen 0 1600x1000x24 -nolisten tcp > $RUN/xvfb.log 2>&1 &
    echo $! > $RUN/xvfb.pid
    sleep 1
    env -i HOME=$RUN/home XDG_CONFIG_HOME=$RUN/xdg DISPLAY=$DISP PATH=$RUN/bin:/usr/bin:/bin SHELL=/bin/sh \
      AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_ROOT=/home/mano/.dotnet IDEVELOP_N1_PROBE=$RUN/probe IDEVELOP_N1_OPTION=${OPT:-a} LANG=en_US.UTF-8 \
      $WT/src/IDevelop.Desktop/bin/Release/net10.0/IDevelop.Desktop $RUN/project > $RUN/app.log 2>&1 &
    echo $! > $RUN/app.pid
    for i in $(seq 1 60); do DISPLAY=$DISP xdotool search --name "iDevelop" >/dev/null 2>&1 && break; sleep 0.5; done
    sleep 2
    DISPLAY=$DISP xdotool search --name "iDevelop" getwindowname 2>/dev/null
    ;;
  cmd)
    shift
    rm -f $RUN/probe/ack
    printf '%s\n' "$@" > $RUN/probe/cmd
    for i in $(seq 1 150); do [ -f $RUN/probe/ack ] && break; sleep 0.2; done
    sleep 0.8
    cat $RUN/probe/ack; echo
    ;;
  shot)  # shot out.png W H
    mkdir -p "$(dirname "$2")"
    DISPLAY=$DISP xdotool mousemove 1599 600; sleep 0.3; DISPLAY=$DISP import -window root -crop "${3}x${4}+0+0" +repage "$2"
    ;;
  hover) # hover "<title>": rest the real pointer on the card until its tooltip shows
    shift
    rm -f $RUN/probe/ack; printf 'hover %s\n' "$1" > $RUN/probe/cmd
    for i in $(seq 1 50); do [ -f $RUN/probe/ack ] && break; sleep 0.2; done
    read -r _ x y < $RUN/probe/ack
    DISPLAY=$DISP xdotool mousemove $((x-20)) $y; sleep 0.2; DISPLAY=$DISP xdotool mousemove $x $y; sleep 1.5
    ;;
  shotkeep)  # shot without moving the pointer
    DISPLAY=$DISP import -window root -crop "${3}x${4}+0+0" +repage "$2"
    ;;
  stop)
    [ -f $RUN/app.pid ] && kill $(cat $RUN/app.pid) 2>/dev/null; sleep 1.5
    [ -f $RUN/app.pid ] && kill -9 $(cat $RUN/app.pid) 2>/dev/null
    [ -f $RUN/xvfb.pid ] && kill $(cat $RUN/xvfb.pid) 2>/dev/null
    # Fake clients still waiting on their gates end once their gate folder is gone.
    rm -rf $RUN/gates
    ;;
esac
