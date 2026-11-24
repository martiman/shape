.PHONY: phpstan ecs ecs-fix rector rector-dry check-quick

phpstan:
	vendor/bin/phpstan analyse --memory-limit=512M

ecs:
	vendor/bin/ecs check

ecs-fix:
	vendor/bin/ecs check --fix

rector:
	vendor/bin/rector process

rector-dry:
	vendor/bin/rector process --dry-run

check-quick: phpstan ecs rector-dry
