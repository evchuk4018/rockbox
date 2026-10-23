#!/usr/bin/perl
#             __________               __   ___.
#   Open      \______   \ ____   ____ |  | _\_ |__   _______  ___
#   Source     |       _//  _ \_/ ___\|  |/ /| __ \ /  _ \  \/  /
#   Jukebox    |    |   (  <_> )  \___|    < | \_\ (  <_> > <  <
#   Firmware   |____|_  /\____/ \___  >__|_ \|___  /\____/__/\_ \
#                     \/            \/     \/    \/            \/
#

# addtargetdir.pl - Adds target directory to gcc-generated dependency data

use File::Basename;

my $rbroot = $ARGV[0];
my $builddir = $ARGV[1];
undef $/;

my $target;
my $rootlen = length $rbroot;
my $src;
my $dependencies = <STDIN>;

# Native MinGW GCC emits drive paths, but MSYS2 Make uses /c/ paths.
# MSYS2 Perl can identify as either msys or cygwin. Normalize before the
# target parser sees drive colons, including GCC's escaped-colon form.
if (($^O eq 'msys' || $^O eq 'cygwin') && $ENV{MSYSTEM}) {
    $dependencies =~ s{(?<!\S)([A-Za-z])(?:\\)?:/}{'/' . lc($1) . '/'}ge;
}

# Split the input file on any runs of '\' and whitespace.
for (split(/[\s\\]+/m, $dependencies)) {
    /^(\/)?[^:]+(\:)?$/;
# Save target and continue if this item ends in ':'
    if (!($2 && ($target=$&))) {
        $src = $&;
# If $target is set, prefix it with the target path
        if ($target) {
            my $dir = dirname $src;
	    substr($dir, 0, $rootlen) = $builddir;
            print "\n$dir/$target";
            $target = "";
# Otherwise, check for an incomplete path for the source file
        } elsif (!$1) {
            $src = "$builddir/$src";
        }
        print " \\\n  $src";
    }
}
print "\n";
