#!/usr/bin/env bash
# Fails when C# under ui/ hand-writes native interop outside the allowlist.
#
# All native calls from C# go through ui/Prisc.Interop, whose bindings are
# generated from crates/prisc_interop (cargo codegen). Everything else under
# ui/ must not declare or call natives itself. This script scans every text
# file under ui/ that git tracks or would track (untracked files included,
# ignored ones such as bin/ and obj/ skipped) for these patterns, by name:
#
#   extern                    \bextern\b
#   DllImport                 DllImport
#   LibraryImport             LibraryImport
#   ComImport                 ComImport
#   delegate-unmanaged        delegate\s*\*\s*unmanaged
#   UnmanagedCallersOnly      UnmanagedCallersOnly
#   UnmanagedFunctionPointer  UnmanagedFunctionPointer
#   Marshal                   Marshal\.(GetDelegateForFunctionPointer|GetFunctionPointerForDelegate|
#                             AddRef|Release|QueryInterface|GetObjectForIUnknown|GetIUnknownForObject)
#   NativeLibrary             NativeLibrary\.
#   objc                      objc_|sel_registerName
#
# Comments count: a line is a match wherever the text appears.
#
# A match is allowed only when a line in tools/native-interop-allowlist/*.txt
# covers its (path, pattern) pair. Allowlist line format, whitespace separated:
#
#   <path glob> <pattern name or *> <one-sentence justification>
#
# Globs are repo-relative: `*` matches within one path segment, `**` across
# segments, `?` one character. `#` starts a comment line; blank lines are
# skipped. permanent.txt holds the exceptions that stay for good; every other
# file belongs to one work package area and lists today's hand-written sites
# of that area, and the area deletes its file once those sites are gone.
#
# Errors (exit 1): a match no line covers; a malformed line (missing
# justification, unknown pattern name, an em-dash); an area-file line whose
# glob names no file under ui/ (the sites moved or went, so the line is stale).
# Warnings: a line that covers no match, and a permanent.txt glob that names
# no file yet. `--strict` turns warnings into errors.
#
# Usage: tools/check-native-interop.sh [--strict]
# Works on macOS (bash 3.2), Linux and Windows git bash; needs git and perl.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)

strict=0
for arg in "$@"; do
  case "$arg" in
    --strict) strict=1 ;;
    -h|--help) sed -n '2,42p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

allow_dir="$root/tools/native-interop-allowlist"
[ -d "$allow_dir" ] || { echo "error: $allow_dir is missing" >&2; exit 2; }

cd "$root"
# NUL-separated so paths with spaces survive; -co adds untracked, non-ignored files.
git ls-files -z -co --exclude-standard -- ui |
  STRICT="$strict" ALLOW_DIR="tools/native-interop-allowlist" perl -e '
use strict;
use warnings;

my @patterns = (
  ["extern",                   qr/\bextern\b/],
  ["DllImport",                qr/DllImport/],
  ["LibraryImport",            qr/LibraryImport/],
  ["ComImport",                qr/ComImport/],
  ["delegate-unmanaged",       qr/delegate\s*\*\s*unmanaged/],
  ["UnmanagedCallersOnly",     qr/UnmanagedCallersOnly/],
  ["UnmanagedFunctionPointer", qr/UnmanagedFunctionPointer/],
  ["Marshal",                  qr/Marshal\.(?:GetDelegateForFunctionPointer|GetFunctionPointerForDelegate|AddRef|Release|QueryInterface|GetObjectForIUnknown|GetIUnknownForObject)/],
  ["NativeLibrary",            qr/NativeLibrary\./],
  ["objc",                     qr/objc_|sel_registerName/],
);
my %known = map { $_->[0] => 1 } @patterns;
my $strict = $ENV{STRICT};
my $allow_dir = $ENV{ALLOW_DIR};
my $gha = ($ENV{GITHUB_ACTIONS} // "") eq "true";
my $em = "\xe2\x80\x94";
my ($errors, $warnings) = (0, 0);

sub error_at {
  my ($file, $line, $msg) = @_;
  $errors++;
  print "::error file=$file,line=$line::$msg\n" if $gha;
  print STDERR "$file:$line: error: $msg\n";
}
sub warn_at {
  my ($file, $line, $msg) = @_;
  if ($strict) { error_at($file, $line, $msg); return; }
  $warnings++;
  print "::warning file=$file,line=$line::$msg\n" if $gha;
  print STDERR "$file:$line: warning: $msg\n";
}
sub glob_re {
  my ($g) = @_;
  my $re = "";
  while (length $g) {
    if ($g =~ s{^\*\*/}{}) { $re .= "(?:.*/)?"; }
    elsif ($g =~ s{^\*\*}{}) { $re .= ".*"; }
    elsif ($g =~ s{^\*}{}) { $re .= "[^/]*"; }
    elsif ($g =~ s{^\?}{}) { $re .= "[^/]"; }
    else { $g =~ s{^(.)}{}s; $re .= quotemeta($1); }
  }
  return qr/^$re$/;
}

# The allowlist.
my @rules;
opendir(my $dh, $allow_dir) or die "cannot read $allow_dir: $!\n";
my @lists = sort grep { /\.txt$/ } readdir $dh;
closedir $dh;
for my $list (@lists) {
  my $path = "$allow_dir/$list";
  open(my $fh, "<", $path) or die "cannot read $path: $!\n";
  while (my $text = <$fh>) {
    $text =~ s/\r?\n$//;
    error_at($path, $., "em-dash in the allowlist") if index($text, $em) >= 0;
    next if $text =~ /^\s*(#|$)/;
    my ($glob, $pattern, $why) = $text =~ /^\s*(\S+)\s+(\S+)(?:\s+(.*\S))?\s*$/;
    if (!defined $glob) { error_at($path, $., "malformed line: $text"); next; }
    if ($pattern ne "*" && !$known{$pattern}) {
      error_at($path, $., "unknown pattern \"$pattern\" (one of: " . join(" ", sort keys %known) . ", *)");
      next;
    }
    if (!defined $why || $why !~ /\w/) { error_at($path, $., "no justification for $glob $pattern"); next; }
    if ($glob !~ m{^ui/}) { error_at($path, $., "glob must start with ui/: $glob"); next; }
    push @rules, { list => $list, path => $path, line => $., glob => $glob, re => glob_re($glob),
                   pattern => $pattern, files => 0, hits => 0 };
  }
  close $fh;
}

# The scan.
my @files = do { local $/ = "\0"; map { chomp; $_ } <STDIN> };
my $sites = 0;
for my $file (@files) {
  next unless -f $file;
  my @mine = grep { $file =~ $_->{re} } @rules;
  $_->{files}++ for @mine;
  next unless -T $file;
  open(my $fh, "<", $file) or die "cannot read $file: $!\n";
  while (my $text = <$fh>) {
    $text =~ s/\r?\n$//;
    for my $p (@patterns) {
      next unless $text =~ $p->[1];
      $sites++;
      my @cover = grep { $_->{pattern} eq "*" || $_->{pattern} eq $p->[0] } @mine;
      if (@cover) { $_->{hits}++ for @cover; next; }
      (my $shown = $text) =~ s/^\s+//;
      error_at($file, $., "pattern $p->[0] is not allowlisted: $shown");
    }
  }
  close $fh;
}

for my $r (@rules) {
  if ($r->{files} == 0) {
    my $msg = "allowlist line names no file: $r->{glob} $r->{pattern}";
    if ($r->{list} eq "permanent.txt") { warn_at($r->{path}, $r->{line}, $msg); }
    else { error_at($r->{path}, $r->{line}, "$msg (stale: update or delete this line)"); }
  } elsif ($r->{hits} == 0) {
    warn_at($r->{path}, $r->{line}, "allowlist line covers no match: $r->{glob} $r->{pattern}");
  }
}

my $nfiles = scalar @files;
my $nrules = scalar @rules;
if ($errors) {
  print STDERR "check-native-interop: $errors error(s), $warnings warning(s) ($nfiles files, $sites matches, $nrules allowlist lines)\n";
  print STDERR "Natives belong in crates/prisc_interop, reached through ui/Prisc.Interop (crates/prisc_interop/README.md).\n";
  exit 1;
}
print "check-native-interop: ok, $warnings warning(s) ($nfiles files, $sites matches, $nrules allowlist lines)\n";
'
