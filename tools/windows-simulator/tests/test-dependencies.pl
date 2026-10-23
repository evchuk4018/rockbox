#!/usr/bin/perl
use strict;
use warnings;
use Test::More;
use FindBin;
use IPC::Open3;
use Symbol 'gensym';

my $script = "$FindBin::Bin/../../addtargetdir.pl";
my $root = '/c/RockboxDev/rockbox';
my $build = '/c/RockboxDev/build-sansaclip-sim';
my $expected = "$build/apps/action.o: $root/apps/action.c " .
    "$root/firmware/export/config.h $build/autoconf.h " .
    "$build/lang/lang.h $build/bitmaps/rockboxlogo.h " .
    "$build/apps/bookmark.o: $root/apps/bookmark.c " .
    "$root/apps/bookmark.h $build/lang/lang.h";

sub read_fixture {
    my ($name) = @_;
    open my $fh, '<', "$FindBin::Bin/fixtures/$name.d" or die $!;
    local $/;
    return <$fh>;
}

sub convert {
    my ($input, $host, $msystem) = @_;
    local $ENV{MSYSTEM};
    if (defined $msystem) { $ENV{MSYSTEM} = $msystem; }
    else { delete $ENV{MSYSTEM}; }
    my $error = gensym;
    my $pid = open3(my $in, my $out, $error, $^X, '-e',
        'local $^O = shift; my $script = shift; do $script; die $@ if $@;',
        $host, $script, $root, $build);
    print {$in} $input;
    close $in;
    local $/;
    my $result = <$out> // '';
    my $errors = <$error> // '';
    waitpid $pid, 0;
    die "Converter failed ($?): $errors" if $?;
    $result =~ s/\\\s+/ /g;
    $result =~ s/\s+/ /g;
    $result =~ s/^ | $//g;
    return $result;
}

for my $fixture (qw(native escaped-colon)) {
    is(convert(read_fixture($fixture), 'cygwin', 'UCRT64'), $expected,
       "$fixture: maps source targets and generated headers under actual MSYS2 Perl host");
}

my $lowercase = read_fixture('native');
$lowercase =~ s/C:/c:/g;
is(convert($lowercase, 'cygwin', 'UCRT64'), $expected,
   'lowercase drive letters retain the same MSYS paths');
is(convert(read_fixture('native'), 'msys', 'UCRT64'), $expected,
   'msys host identity is supported');

for my $host (qw(cygwin msys linux darwin)) {
    is(convert(read_fixture('posix'), $host, 'UCRT64'), $expected,
       "existing POSIX dependencies remain unchanged on $host");
}

is(convert(read_fixture('posix'), 'cygwin', undef), $expected,
   'ordinary Cygwin without MSYSTEM retains existing behavior');

my $target_only = convert(read_fixture('native'), 'cygwin', 'UCRT64');
like($target_only, qr{\Q$build/apps/action.o: \E.*\Q$build/lang/lang.h\E},
     'action.o explicitly depends on generated lang.h');
like($target_only, qr{\Q$build/apps/action.o: \E.*\Q$build/bitmaps/rockboxlogo.h\E},
     'action.o explicitly depends on generated bitmap header');
unlike($target_only, qr{(?:^|\s)[A-Za-z](?:\\)?:[\\/]},
       'no drive-colon dependency tokens reach the old parser');

done_testing();
