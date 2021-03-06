module "bigfoodswitch" {
  source = "../modules/redirectrule"

  alb               = aws_alb.api-collaction
  alb_listener      = aws_alb_listener.api-collaction
  route53_zone_name = "bigfoodswitch.org."
  domain            = "www.bigfoodswitch.org"
  redirect_to_path  = "/crowdactions/the-big-food-switch/201"
}

module "bigfoodswitch_sub" {
  source = "../modules/redirectrule"

  alb               = aws_alb.api-collaction
  alb_listener      = aws_alb_listener.api-collaction
  route53_zone_name = "collaction.org."
  domain            = "bigfoodswitch.collaction.org"
  redirect_to_path  = "/crowdactions/the-big-food-switch/201"
}
