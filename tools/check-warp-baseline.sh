#!/usr/bin/env bash
# Compares the GPU-on-WARP test results with the recorded baseline.
#
# CI's Windows runners have no GPU, so the D3D12 paths only run on WARP, the
# software adapter. The set of filtered GPU tests that must pass there is the
# set that passed on BASE, with the old C# D3D12 code, before any area moved
# it to prisc_interop. Some of those tests may never have passed on WARP: the
# old code skipped software adapters, so a BASE failure is a result to record,
# not a regression. This script reads the trx a `dotnet test --logger trx` run
# wrote and checks it against tools/warp-baseline.txt, one test per line:
#
#   <Passed|Failed|Skipped> <fully qualified test name>
#
# `#` starts a comment line; blank lines are skipped. A trx outcome of
# NotExecuted counts as Skipped; anything else that is not Passed (Error,
# Timeout, Aborted, ...) counts as Failed.
#
# Errors (exit 1): a test the baseline records as Passed that now fails, skips
# or is missing; a failing test the baseline does not list (a new test must
# pass or skip); an unreadable trx with no results; a malformed baseline line.
# Warnings: a test that still fails as it did on BASE.
# Notices: a test that passes now but did not on BASE, a new skipped test, and
# a baseline test that no longer runs (renamed or removed); update the
# baseline for those.
#
# A baseline with no entries is record mode: nothing fails on outcomes, every
# failure is printed as a warning, and <observed out> is the file to commit
# as tools/warp-baseline.txt. Either way <observed out> receives this run's
# outcomes in the baseline format.
#
# Usage: tools/check-warp-baseline.sh <results.trx> <baseline> <observed out>
# Works on macOS (bash 3.2), Linux and Windows git bash; needs perl.
set -euo pipefail

if [ $# -ne 3 ]; then
  sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
fi
trx=$1 baseline=$2 observed=$3
[ -f "$trx" ] || { echo "::error::$trx does not exist; did the WARP test run build and start?"; exit 1; }
[ -f "$baseline" ] || { echo "::error::$baseline does not exist"; exit 1; }

TRX="$trx" BASELINE="$baseline" OBSERVED="$observed" perl -e '
use strict;
use warnings;

my %rank = (Passed => 0, Skipped => 1, Failed => 2);
sub decode {
  my $s = shift;
  $s =~ s/&lt;/</g; $s =~ s/&gt;/>/g; $s =~ s/&quot;/"/g; $s =~ s/&apos;/\x27/g;
  $s =~ s/&#x([0-9A-Fa-f]+);/chr(hex($1))/ge; $s =~ s/&#([0-9]+);/chr($1)/ge;
  $s =~ s/&amp;/&/g;
  return $s;
}

# This run: test name -> outcome. A name that ran more than once keeps its worst outcome.
open(my $t, "<", $ENV{TRX}) or die "cannot read $ENV{TRX}: $!\n";
my $xml = do { local $/; <$t> };
close($t);
my %now;
while ($xml =~ /<UnitTestResult\b([^>]*)>/g) {
  my $attrs = $1;
  my ($name) = $attrs =~ /\btestName="([^"]*)"/;
  my ($outcome) = $attrs =~ /\boutcome="([^"]*)"/;
  next unless defined $name && defined $outcome;
  $name = decode($name);
  $name =~ s/\s+/ /g;
  my $o = $outcome eq "Passed" ? "Passed" : $outcome eq "NotExecuted" ? "Skipped" : "Failed";
  $now{$name} = $o if !exists $now{$name} || $rank{$o} > $rank{$now{$name}};
}
if (!%now) {
  print "::error::$ENV{TRX} holds no test results; did the filter match nothing or the run abort?\n";
  exit 1;
}

# The baseline.
open(my $b, "<", $ENV{BASELINE}) or die "cannot read $ENV{BASELINE}: $!\n";
my (%base, $bad);
while (my $line = <$b>) {
  $line =~ s/\r?\n\z//;
  next if $line =~ /^\s*(#|\z)/;
  if ($line =~ /^(Passed|Failed|Skipped)\s+(\S.*?)\s*\z/) {
    $base{$2} = $1;
  } else {
    print "::error::$ENV{BASELINE}:$.: expected \"<Passed|Failed|Skipped> <test name>\", got: $line\n";
    $bad = 1;
  }
}
close($b);
exit 1 if $bad;

open(my $o, ">", $ENV{OBSERVED}) or die "cannot write $ENV{OBSERVED}: $!\n";
print $o "# GPU-on-WARP outcomes of one CI run (tools/check-warp-baseline.sh).\n";
print $o "$now{$_} $_\n" for sort keys %now;
close($o);

my %count = (Passed => 0, Skipped => 0, Failed => 0);
$count{$now{$_}}++ for keys %now;
print "WARP results: $count{Passed} passed, $count{Failed} failed, $count{Skipped} skipped\n";

if (!%base) {
  print "::warning::no WARP baseline is recorded yet ($ENV{BASELINE} lists no tests), so no outcome fails this run. "
    . "On BASE, commit this run\x27s $ENV{OBSERVED} as $ENV{BASELINE}.\n";
  for (sort keys %now) { print "::warning::WARP: $_ failed (not gated: no baseline yet)\n" if $now{$_} eq "Failed"; }
  exit 0;
}

my $errors = 0;
for my $name (sort keys %base) {
  my $was = $base{$name};
  my $is = $now{$name} // "missing";
  if ($was eq "Passed" && $is ne "Passed") {
    print "::error::WARP: $name passed on BASE and is now $is\n";
    $errors++;
  } elsif ($was ne "Passed" && $is eq "Passed") {
    print "::notice::WARP: $name passes now ($was on BASE); update the baseline\n";
  } elsif ($was eq "Failed" && $is eq "Failed") {
    print "::warning::WARP: $name still fails, as it did on BASE\n";
  } elsif ($is eq "missing") {
    print "::notice::WARP: $name ($was on BASE) no longer runs; renamed or removed?\n";
  }
}
for my $name (sort grep { !exists $base{$_} } keys %now) {
  if ($now{$name} eq "Failed") {
    print "::error::WARP: $name is not in the baseline and fails; a new test must pass or skip\n";
    $errors++;
  } elsif ($now{$name} eq "Skipped") {
    print "::notice::WARP: $name is not in the baseline and skipped\n";
  }
}
if ($errors) {
  print "::error::$errors WARP regression(s) against $ENV{BASELINE}\n";
  exit 1;
}
print "WARP results match the baseline\n";
'
