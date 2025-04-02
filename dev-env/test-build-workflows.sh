#! /bin/bash

docker compose -f build-images-compose.yml \
  up --build --remove-orphans --detach
