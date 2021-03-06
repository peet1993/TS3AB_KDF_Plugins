#!/bin/bash

error() {
	if [ -z "$1" ]; then
		echo "Missing error message." 2>&1
		exit 1
	fi

	echo -e "\033[1;31m$1\033[0m" 2>&1
	exit 1
}

isGitUpToDate() {
	git remote update > /dev/null 2>&1

	LOCAL=$(git rev-parse @)
	REMOTE=$(git rev-parse origin/master)

	if [ "$LOCAL" = "$REMOTE" ]; then
		# Up to date
		return 0 # Success exit code
	else
		# Not up to date
		return 1 # Error exit code
	fi
}

compileBot() {
	git pull || error "Failed to pull repository."
	git submodule update --recursive || error "Failed to update submodules."
	rm -rf TS3AudioBot/bin/*
	dotnet build --framework netcoreapp3.1 --configuration "$1" TS3AudioBot || error "Compilation of TS3AudioBot failed."
	rsync -a --progress --exclude=NLog.config TS3AudioBot/bin/"$1"/netcoreapp3.1/ ../../ || error "RSync failed."
	cd ..
	echo -e "\033[1;33mFinished building the bot!\033[0m"
	echo "---------------------------------------------------"
}

if [ "$1" == "-f" ]; then
	force=1
fi

if [ "$2" == "-f" ]; then
	force=1
fi

if [ "$1" == "Debug" ]; then
	buildtype="$1"
else
	buildtype="Release"
fi

# Recompile the bot
echo -e "\033[1;33mRecompiling the bot...\033[0m"
if ! [ -d TS3AudioBot ]; then
	git clone --recursive https://github.com/jwiesler/TS3AudioBot.git || error "Failed to clone repository."
	cd TS3AudioBot || error "Failed to cd into the bot repository."
	compileBot "$buildtype"
else
	cd TS3AudioBot || error "Failed to cd into the bot repository."

	if [ "$force" ] || ! isGitUpToDate; then
		compileBot "$buildtype"
	else
		cd ..
		echo "Nothing to do."
	fi
fi

for plugin in */; do
	plugin=${plugin%/}

	# Skip the bot repository
	if [ "$plugin" == "TS3AudioBot" ]; then
		continue
	fi

	echo -e "\033[1;33mBuilding plugin $plugin...\033[0m"

	cd "$plugin" || error "Failed to cd into plugin folder '$plugin'"

	# Build
	dotnet build --framework netcoreapp3.1 --configuration "$buildtype" || error "Compilation of plugin '$plugin' failed."

	# Move plugin to toplevel plugin folder
	mv "bin/$buildtype/netcoreapp3.1/$plugin.dll" ../ || error "Moving the .dll failed."

	# Remove build directory
	rm -r bin/

	cd ..

	echo -e "\033[1;33mFinished building plugin $plugin!\033[0m"
	echo "---------------------------------------------------"
done
